// Zero-dependency test harness.
//
// A test framework would read better, but this project's whole point is that the build
// pipeline stays runnable anywhere — including a locked-down build agent that cannot
// restore packages. "dotnet run --project src/SteamPipeStudio.Tests" is the whole story,
// and a non-zero exit code fails a CI step exactly like any other runner.
//
// The .vdf files under fixtures/ are the sample scripts shipped in the Steamworks SDK
// (sdk/tools/ContentBuilder/scripts). They are the real thing the parser has to survive.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SteamPipeStudio.Core.Build;
using SteamPipeStudio.Core.Ci;
using SteamPipeStudio.Core.Model;
using SteamPipeStudio.Core.Security;
using SteamPipeStudio.Core.Steam;
using SteamPipeStudio.Core.Vdf;

internal static class Harness
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    public static void Check(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; return; }
        Failures.Add(detail is null ? name : $"{name} — {detail}");
    }

    public static void Equal<T>(string name, T expected, T actual) =>
        Check(name, EqualityComparer<T>.Default.Equals(expected, actual),
              $"expected <{expected}>, got <{actual}>");

    public static void Throws<TException>(string name, Action action) where TException : Exception
    {
        try { action(); Check(name, false, $"expected {typeof(TException).Name}, nothing was thrown"); }
        catch (TException) { _passed++; }
        catch (Exception e) { Check(name, false, $"expected {typeof(TException).Name}, got {e.GetType().Name}: {e.Message}"); }
    }

    public static int Report()
    {
        Console.WriteLine($"\n{_passed} passed, {Failures.Count} failed");
        foreach (var failure in Failures) Console.WriteLine("  FAIL  " + failure);
        return Failures.Count == 0 ? 0 : 1;
    }
}

internal static class Program
{
    private static string Fixture(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static int Main()
    {
        // The suite must give the same answer on an Italian machine as on an American
        // one. It did not: FormatBytes picked up the ambient culture, so "1.5 KB" came
        // out as "1,5 KB" and the assertion failed on a machine set to it-IT. Pinning
        // the culture keeps the harness itself deterministic; the explicit it-IT case in
        // Preflight() is what proves the formatter no longer cares either way.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        VdfParsing();
        VdfRoundTrip();
        Generation();
        Import();
        Validation();
        Preflight();
        OutputParsing();
        Download();
        CiExport();
        Locator();
        ConsoleLogDeduplication();
        Secrets();
        return Harness.Report();
    }

    // ------------------------------------------------------------------

    private static void VdfParsing()
    {
        Console.WriteLine("== VDF parsing ==");

        var simple = VdfParser.ParseFile(Fixture("simple_app_build.vdf"));
        Harness.Equal("root key", "AppBuild", simple.Key);
        Harness.Equal("AppID", 1000u, simple.GetUInt("AppID"));
        Harness.Equal("preview flag", false, simple.GetBool("preview"));

        // Valve's samples mix casing; lookups must not care.
        Harness.Check("case-insensitive lookup", simple.GetString("appid") == "1000");
        Harness.Equal("comment captured", "your AppID", simple.Find("AppID")!.Comment);

        var depots = simple.Find("Depots");
        Harness.Check("Depots block found", depots is { IsBlock: true });
        Harness.Equal("inline depot count", 1, depots!.Children.Count);

        // The single most important property: duplicate keys survive.
        var depot1002 = VdfParser.ParseFile(Fixture("depot_build_1002.vdf"));
        Harness.Equal("three FileMapping blocks", 3, depot1002.FindAll("FileMapping").Count());
        Harness.Equal("three FileExclusion values", 3, depot1002.FindAll("FileExclusion").Count());
        Harness.Equal("InstallScript", "localization\\german\\german_installscript.vdf",
                      depot1002.GetString("InstallScript"));

        // Backslash paths must survive: escape sequences are off, matching steamcmd.
        var app1000 = VdfParser.ParseFile(Fixture("app_build_1000.vdf"));
        Harness.Equal("backslash content root", "..\\content\\", app1000.GetString("ContentRoot"));
        Harness.Equal("trailing-backslash path parses", "D:\\build_output\\", app1000.GetString("BuildOutput"));
        Harness.Equal("SetLive", "AlphaTest", app1000.GetString("SetLive"));
        Harness.Equal("referenced depot script", "depot_build_1001.vdf",
                      app1000.Find("Depots")!.GetString("1001"));

        // Error handling
        Harness.Throws<VdfParseException>("unterminated block", () => VdfParser.Parse("\"A\" {"));
        Harness.Throws<VdfParseException>("unterminated string", () => VdfParser.Parse("\"A\" \"b"));
        Harness.Throws<VdfParseException>("empty document", () => VdfParser.Parse("   // nothing\n"));

        // A BOM must not become part of the first key.
        var withBom = VdfParser.Parse("\uFEFF\"AppBuild\"\n{\n\"AppID\" \"7\"\n}\n");
        Harness.Equal("BOM stripped", "AppBuild", withBom.Key);

        // Conditionals are preserved rather than dropped.
        var conditional = VdfParser.Parse("\"Root\"\n{\n\"file\" \"a.dll\" [$WIN32]\n}\n");
        Harness.Equal("condition preserved", "[$WIN32]", conditional.Children[0].Condition);
    }

    private static void VdfRoundTrip()
    {
        Console.WriteLine("== VDF round-trip ==");

        foreach (var name in new[]
                 {
                     "simple_app_build.vdf", "app_build_1000.vdf",
                     "depot_build_1001.vdf", "depot_build_1002.vdf"
                 })
        {
            var original = VdfParser.ParseFile(Fixture(name));
            var reparsed = VdfParser.Parse(VdfWriter.Write(original));
            Harness.Check($"round-trip {name}", TreesMatch(original, reparsed));
        }

        // A value containing a quote cannot be represented and must fail loudly
        // rather than silently producing a file steamcmd will misparse.
        var node = VdfNode.Block("AppBuild");
        node.Add("Desc", "he said \"ship it\"");
        Harness.Throws<InvalidOperationException>("quote in value rejected", () => VdfWriter.Write(node));

        Harness.Equal("path normalisation", "../content/", VdfWriter.NormalisePath("..\\content\\"));
    }

    private static bool TreesMatch(VdfNode a, VdfNode b)
    {
        if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal)) return false;
        if (a.IsBlock != b.IsBlock) return false;
        if (!a.IsBlock) return string.Equals(a.Value, b.Value, StringComparison.Ordinal);
        if (a.Children.Count != b.Children.Count) return false;
        for (var i = 0; i < a.Children.Count; i++)
            if (!TreesMatch(a.Children[i], b.Children[i])) return false;
        return true;
    }

    // ------------------------------------------------------------------

    private static BuildProfile SampleProfile() => new()
    {
        Name = "Sample",
        AppId = 480,
        Description = "nightly",
        ContentRoot = "/games/sample/content",
        BuildOutput = "/games/sample/output",
        SteamAccountName = "builder",
        SetLiveBranch = "beta",
        Depots =
        {
            new DepotDefinition
            {
                DepotId = 481,
                FileMappings = { new FileMappingRule { LocalPath = "*", DepotPath = ".", Recursive = true } },
                FileExclusions = { "*.pdb" }
            },
            new DepotDefinition
            {
                DepotId = 482,
                FileMappings =
                {
                    new FileMappingRule { LocalPath = "bin/*", DepotPath = "executables", Recursive = true }
                }
            }
        }
    };

    private static void Generation()
    {
        Console.WriteLine("== Script generation ==");

        var profile = SampleProfile();
        var scripts = BuildScriptGenerator.Generate(profile);

        Harness.Equal("one app + two depot scripts", 3, scripts.Count);
        Harness.Equal("app script name", "app_build_480.vdf", scripts[0].FileName);

        var app = VdfParser.Parse(scripts[0].Contents);
        Harness.Equal("generated AppID", 480u, app.GetUInt("AppID"));
        Harness.Equal("generated SetLive", "beta", app.GetString("SetLive"));
        Harness.Equal("forward-slash content root", "/games/sample/content", app.GetString("ContentRoot"));
        Harness.Equal("depot reference", "depot_build_481.vdf", app.Find("Depots")!.GetString("481"));
        Harness.Check("Local omitted when unset", app.Find("Local") is null);

        // "public" cannot be set live from a build script — Steam rejects it, so the
        // key must not be emitted at all.
        var publicProfile = SampleProfile();
        publicProfile.SetLiveBranch = "public";
        var publicApp = VdfParser.Parse(BuildScriptGenerator.Generate(publicProfile)[0].Contents);
        Harness.Check("SetLive public omitted", publicApp.Find("SetLive") is null);

        var depot = VdfParser.Parse(scripts[1].Contents);
        Harness.Equal("generated DepotID", 481u, depot.GetUInt("DepotID"));
        Harness.Equal("exclusion written", "*.pdb", depot.GetString("FileExclusion"));
        Harness.Equal("mapping recursive", true, depot.FindAll("FileMapping").First().GetBool("Recursive"));

        // Recursive is meaningless without a wildcard and should not be emitted.
        var exact = new DepotDefinition
        {
            DepotId = 9,
            FileMappings = { new FileMappingRule { LocalPath = "readme.txt", DepotPath = ".", Recursive = true } }
        };
        var exactNode = BuildScriptGenerator.BuildDepotNode(exact);
        Harness.Check("Recursive omitted without wildcard",
                      exactNode.FindAll("FileMapping").First().Find("Recursive") is null);

        Harness.Equal("description flattened", "a 'quoted' line",
                      BuildScriptGenerator.SanitiseDescription("a \"quoted\"\nline"));
        Harness.Equal("description capped", 250,
                      BuildScriptGenerator.SanitiseDescription(new string('x', 400)).Length);
    }

    private static void Import()
    {
        Console.WriteLine("== Import existing SDK scripts ==");

        var profile = BuildScriptGenerator.ImportAppScript(Fixture("app_build_1000.vdf"));
        Harness.Equal("imported AppID", 1000u, profile.AppId);
        Harness.Equal("imported SetLive", "AlphaTest", profile.SetLiveBranch);
        Harness.Equal("imported preview", true, profile.Preview);
        Harness.Equal("imported depot count", 2, profile.Depots.Count);

        var depot1002 = profile.Depots.Single(d => d.DepotId == 1002);
        Harness.Equal("imported mappings", 3, depot1002.FileMappings.Count);
        Harness.Equal("imported exclusions", 3, depot1002.FileExclusions.Count);
        Harness.Equal("imported file properties", 1, depot1002.FileProperties.Count);
        Harness.Equal("imported attributes", "userconfig", depot1002.FileProperties[0].Attributes);
        Harness.Equal("mapping target", "executables\\", depot1002.FileMappings[0].DepotPath);

        // ContentRoot in the sample is relative; import resolves it against the script.
        Harness.Check("content root made absolute", Path.IsPathRooted(profile.ContentRoot));

        // Inline-depot layout (simple_app_build.vdf) must import too.
        var simple = BuildScriptGenerator.ImportAppScript(Fixture("simple_app_build.vdf"));
        Harness.Equal("inline depot imported", 1, simple.Depots.Count);
        Harness.Equal("inline depot id", 1001u, simple.Depots[0].DepotId);
        Harness.Equal("inline mapping recursive", true, simple.Depots[0].FileMappings[0].Recursive);

        Harness.Throws<InvalidDataException>("wrong root rejected",
            () => BuildScriptGenerator.ImportAppScript(Fixture("depot_build_1001.vdf")));
    }

    private static void Validation()
    {
        Console.WriteLine("== Validation ==");

        var root = Path.Combine(Path.GetTempPath(), "sps-validate-" + Guid.NewGuid().ToString("N"));
        var content = Path.Combine(root, "content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "game.exe"), "x");

        var settings = new AppSettings { ContentBuilderPath = root };

        var profile = SampleProfile();
        profile.ContentRoot = content;
        profile.BuildOutput = Path.Combine(content, "output"); // deliberately wrong

        var issues = BuildValidator.Validate(profile, settings);
        Harness.Check("cache-inside-content is an error",
            issues.Any(i => i.Severity == IssueSeverity.Error && i.Field == "BuildOutput"));
        Harness.Check("missing steamcmd is an error",
            issues.Any(i => i.Field == "ContentBuilder"));
        Harness.Check("blocking issues detected", BuildValidator.HasBlockingIssues(issues));

        profile.BuildOutput = Path.Combine(root, "output");
        var fixedIssues = BuildValidator.Validate(profile, settings);
        Harness.Check("cache moved out resolves the error",
            !fixedIssues.Any(i => i.Field == "BuildOutput" && i.Severity == IssueSeverity.Error));

        // Empty content folder must block, not upload an empty build.
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        profile.ContentRoot = empty;
        Harness.Check("empty content root blocks",
            BuildValidator.Validate(profile, settings)
                .Any(i => i.Field == "ContentRoot" && i.Severity == IssueSeverity.Error));

        // Duplicate depot IDs
        var duplicate = SampleProfile();
        duplicate.Depots[1].DepotId = duplicate.Depots[0].DepotId;
        Harness.Check("duplicate depot detected",
            BuildValidator.Validate(duplicate, settings).Any(i => i.Message.Contains("more than once")));

        // Branch with a space
        var spaced = SampleProfile();
        spaced.SetLiveBranch = "my branch";
        Harness.Check("branch with space rejected",
            BuildValidator.Validate(spaced, settings)
                .Any(i => i.Field == "SetLive" && i.Severity == IssueSeverity.Error));

        Harness.Check("IsInside positive", BuildValidator.IsInside(Path.Combine(root, "a", "b"), root));
        Harness.Check("IsInside negative", !BuildValidator.IsInside(root, Path.Combine(root, "a")));
        Harness.Check("IsInside sibling prefix", !BuildValidator.IsInside(root + "-other", root));

        Directory.Delete(root, true);
    }

    private static void Preflight()
    {
        Console.WriteLine("== Content preflight ==");

        var root = Path.Combine(Path.GetTempPath(), "sps-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "bin", "tools"));
        Directory.CreateDirectory(Path.Combine(root, "data"));

        File.WriteAllText(Path.Combine(root, "game.exe"), new string('a', 100));
        File.WriteAllText(Path.Combine(root, "game.pdb"), new string('a', 50));
        File.WriteAllText(Path.Combine(root, "bin", "engine.dll"), new string('a', 200));
        File.WriteAllText(Path.Combine(root, "bin", "engine.pdb"), new string('a', 10));
        File.WriteAllText(Path.Combine(root, "bin", "tools", "editor.exe"), new string('a', 10));
        File.WriteAllText(Path.Combine(root, "data", "assets.pak"), new string('a', 400));

        var profile = new BuildProfile
        {
            AppId = 480,
            ContentRoot = root,
            Depots = { DepotDefinition.Create(481) }
        };

        var all = ContentPreflight.Run(profile);
        Harness.Equal("all six files matched by '*'", 6, all.FileCount);
        Harness.Equal("total size", 770L, all.TotalBytes);
        Harness.Check("symbols flagged", all.Depots[0].Notes.Any(n => n.Contains("*.pdb")));

        // Exclusions
        profile.Depots[0].FileExclusions.Add("*.pdb");
        profile.Depots[0].FileExclusions.Add("bin/tools*");
        var excluded = ContentPreflight.Run(profile);
        Harness.Equal("exclusions applied", 3, excluded.FileCount);
        Harness.Check("no symbol warning after exclusion",
            !excluded.Depots[0].Notes.Any(n => n.Contains("*.pdb") && n.Contains("uploaded")));

        // Directory-scoped wildcard with remap
        var remapped = new BuildProfile
        {
            AppId = 480,
            ContentRoot = root,
            Depots =
            {
                new DepotDefinition
                {
                    DepotId = 482,
                    FileMappings =
                    {
                        new FileMappingRule { LocalPath = "bin/*", DepotPath = "executables", Recursive = false }
                    }
                }
            }
        };
        var remapResult = ContentPreflight.Run(remapped);
        Harness.Equal("non-recursive bin/* skips subfolder", 2, remapResult.FileCount);
        Harness.Check("depot path remapped",
            remapResult.Depots[0].Files.Any(f => f.DepotPath == "executables/engine.dll"));

        // A mapping that matches nothing is the failure the GUI most needs to surface.
        var broken = new BuildProfile
        {
            AppId = 480,
            ContentRoot = root,
            Depots =
            {
                new DepotDefinition
                {
                    DepotId = 483,
                    FileMappings = { new FileMappingRule { LocalPath = "does-not-exist/*", DepotPath = "." } }
                }
            }
        };
        var brokenResult = ContentPreflight.Run(broken);
        Harness.Equal("no files matched", 0, brokenResult.FileCount);
        Harness.Check("empty mapping reported",
            brokenResult.Depots[0].Notes.Any(n => n.Contains("matched no files")));
        Harness.Check("empty depot reported",
            brokenResult.Depots[0].Notes.Any(n => n.Contains("uploaded empty")));

        // Regression: a depot built with a collection initializer must contain exactly
        // the mappings that were written, not those plus a hidden catch-all.
        var explicitDepot = new DepotDefinition
        {
            DepotId = 484,
            FileMappings = { new FileMappingRule { LocalPath = "data/*", DepotPath = "." } }
        };
        Harness.Equal("no implicit catch-all mapping", 1, explicitDepot.FileMappings.Count);
        Harness.Equal("factory adds one default mapping", 1, DepotDefinition.Create(1).FileMappings.Count);
        Harness.Equal("factory mapping is the content root", "*",
                      DepotDefinition.Create(1).FileMappings[0].LocalPath);

        // Exclusion wildcards cross directories; mapping wildcards do not.
        Harness.Check("exclusion crosses directories",
            ContentPreflight.MatchesExclusion("bin/tools/editor.exe", "bin/tools*"));
        Harness.Check("exclusion matches at depth",
            ContentPreflight.MatchesExclusion("a/b/c/thing.pdb", "*.pdb"));
        Harness.Check("mapping wildcard stays in one directory",
            !ContentPreflight.MatchesMapping("bin/tools/editor.exe",
                new FileMappingRule { LocalPath = "bin/*", DepotPath = ".", Recursive = false }));
        Harness.Check("mapping wildcard descends when recursive",
            ContentPreflight.MatchesMapping("bin/tools/editor.exe",
                new FileMappingRule { LocalPath = "bin/*", DepotPath = ".", Recursive = true }));

        Harness.Equal("byte formatting", "1.5 KB", ContentPreflight.FormatBytes(1536));
        Harness.Equal("byte formatting small", "512 B", ContentPreflight.FormatBytes(512));

        // Regression: a size must not change shape with the machine's regional settings.
        var ambient = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("it-IT");
            Harness.Equal("byte formatting ignores the ambient culture", "1.5 KB",
                          ContentPreflight.FormatBytes(1536));
        }
        finally { CultureInfo.CurrentCulture = ambient; }

        Directory.Delete(root, true);
    }

    private static void OutputParsing()
    {
        Console.WriteLine("== steamcmd output parsing ==");

        var success = SteamCmdOutputParser.Parse("Successfully finished appID 480 - build 12345678");
        Harness.Equal("success detected", SteamCmdEventKind.BuildSucceeded, success.Kind);
        Harness.Equal("build id extracted", 12345678u, success.BuildId);

        // SDK 1.63 dropped the dash, moved the build id into brackets and prefixed the
        // line with a timestamp. Missing this shape reports a finished upload as a
        // failure, which is the worst thing this parser can do.
        var timestamped = SteamCmdOutputParser.Parse(
            "[2026-08-21 15:06:42]: Successfully finished AppID 4370990 build (BuildID 24862532).");
        Harness.Equal("SDK 1.63 success detected", SteamCmdEventKind.BuildSucceeded, timestamped.Kind);
        Harness.Equal("SDK 1.63 build id extracted", 24862532u, timestamped.BuildId);

        var guard = SteamCmdOutputParser.Parse("Steam Guard code:");
        Harness.Equal("steam guard prompt", SteamCmdEventKind.SteamGuardPrompt, guard.Kind);

        var twoFactor = SteamCmdOutputParser.Parse("Two-factor code:");
        Harness.Equal("two-factor prompt", SteamCmdEventKind.SteamGuardPrompt, twoFactor.Kind);

        var failed = SteamCmdOutputParser.Parse("FAILED with result code Invalid Password");
        Harness.Equal("login failure", SteamCmdEventKind.LoginFailed, failed.Kind);

        // steamcmd appends the verdict to the line that announces the attempt. Read off a
        // real run: classifying this as chatter leaves a rejected login with no reason.
        var rejected = SteamCmdOutputParser.Parse(
            "Logging in user 'someone' [U:1:0] to Steam Public...ERROR (Invalid Password)");
        Harness.Equal("login rejection on the attempt line",
            SteamCmdEventKind.LoginFailed, rejected.Kind);
        Harness.Equal("rejection reason extracted", "Invalid Password", rejected.Detail);

        // The same line without a verdict is still just chatter.
        var attempting = SteamCmdOutputParser.Parse(
            "Logging in user 'someone' [U:1:0] to Steam Public...");
        Harness.Equal("login attempt is not a failure", SteamCmdEventKind.Bootstrap, attempting.Kind);
        Harness.Equal("account name extracted", "someone", attempting.Detail);

        var loggedIn = SteamCmdOutputParser.Parse("Waiting for client config...OK");
        Harness.Equal("login success", SteamCmdEventKind.LoginSucceeded, loggedIn.Kind);

        var loginOk = SteamCmdOutputParser.Parse(
            "Logging in user 'someone' [U:1:0] to Steam Public...OK");
        Harness.Equal("login success on the attempt line",
            SteamCmdEventKind.LoginSucceeded, loginOk.Kind);

        // steamcmd's console log is the only channel that carries some output in time to
        // be useful, and every line of it is stamped. Anything anchored to the start of a
        // line has to see through the stamp.
        Harness.Equal("stamp stripped", "password:",
            SteamCmdOutputParser.StripLogTimestamp("[2026-08-21 16:12:10] password:"));
        Harness.Equal("unstamped line unchanged", "password:",
            SteamCmdOutputParser.StripLogTimestamp("  password:  "));

        // steamcmd stamps its own build messages, and those keep a colon after the
        // bracket. Only the log file's outer stamp may go: strip the inner one too and
        // the same message coming from the log and from the pipe stops matching, which
        // prints the entire run twice.
        Harness.Equal("steamcmd's own stamp is part of the message",
            "[2026-08-21 16:27:07]: Starting AppID 2089870 build (flags 0x0).",
            SteamCmdOutputParser.StripLogTimestamp(
                "[2026-08-21 16:27:07]: Starting AppID 2089870 build (flags 0x0)."));
        Harness.Equal("only the outer stamp is removed",
            "[2026-08-21 16:27:07]: Starting AppID 2089870 build (flags 0x0).",
            SteamCmdOutputParser.StripLogTimestamp(
                "[2026-08-21 16:27:07] [2026-08-21 16:27:07]: Starting AppID 2089870 build (flags 0x0)."));

        var stampedPrompt = SteamCmdOutputParser.Parse("[2026-08-21 16:12:10] password: ");
        Harness.Equal("stamped password prompt still detected",
            SteamCmdEventKind.LoginPrompt, stampedPrompt.Kind);

        var stampedSuccess = SteamCmdOutputParser.Parse(
            "[2026-08-21 15:06:42] Successfully finished AppID 4370990 build (BuildID 24862532).");
        Harness.Equal("stamped success still detected",
            SteamCmdEventKind.BuildSucceeded, stampedSuccess.Kind);

        // The console log writes "Loading Steam API..." and "OK" as two lines. Reading
        // that lone OK as a completed login would disarm the wait for the login prompt
        // that is about to appear.
        var lonelyOk = SteamCmdOutputParser.Parse("[2026-08-21 16:12:10] OK");
        Harness.Check("a bare OK is not a login",
            lonelyOk.Kind is not SteamCmdEventKind.LoginSucceeded, lonelyOk.Kind.ToString());

        var bootstrap = SteamCmdOutputParser.Parse("[  4%] Checking for available updates...");
        Harness.Equal("bootstrap progress", SteamCmdEventKind.Bootstrap, bootstrap.Kind);
        Harness.Equal("bootstrap percent", 4d, bootstrap.Percent);

        var uploading = SteamCmdOutputParser.Parse("Uploading depot 481 content, 42.5%");
        Harness.Equal("upload event", SteamCmdEventKind.DepotUploading, uploading.Kind);
        Harness.Equal("upload depot", 481u, uploading.DepotId);
        Harness.Equal("upload percent", 42.5d, uploading.Percent);

        // What SDK 1.65 actually prints while scanning and uploading, read off a real
        // console log: "Scanning content" / "Uploading content..." once, then bare
        // " 40.8MB (45%)" lines between runs of "." — no phase, no depot, just the
        // number. They have to come through as progress or the bar never moves.
        var bareProgress = SteamCmdOutputParser.Parse("[2026-08-21 16:27:21]  40.8MB (45%)");
        Harness.Equal("bare progress line", SteamCmdEventKind.Progress, bareProgress.Kind);
        Harness.Equal("bare progress percent", 45d, bareProgress.Percent);

        var uploadingContent = SteamCmdOutputParser.Parse("[2026-08-21 16:27:08] Uploading content...");
        Harness.Equal("upload phase announced without a depot",
            SteamCmdEventKind.DepotUploading, uploadingContent.Kind);
        Harness.Equal("no depot on the announcement", null, uploadingContent.DepotId);

        var buildingDepot = SteamCmdOutputParser.Parse(
            "[2026-08-21 16:27:07] [2026-08-21 16:27:07]: Building depot 2089871...");
        Harness.Equal("building depot names the depot", SteamCmdEventKind.DepotScanning, buildingDepot.Kind);
        Harness.Equal("depot id from the building line", 2089871u, buildingDepot.DepotId);

        var scanning = SteamCmdOutputParser.Parse("Scanning content");
        Harness.Equal("scan event", SteamCmdEventKind.DepotScanning, scanning.Kind);

        var error = SteamCmdOutputParser.Parse("ERROR! Failed to build depot 481");
        Harness.Check("error surfaced",
            error.Kind is SteamCmdEventKind.BuildFailed or SteamCmdEventKind.Error);

        var noise = SteamCmdOutputParser.Parse("Redirecting stderr to bootstrap_log.txt");
        Harness.Equal("unknown lines pass through", SteamCmdEventKind.Raw, noise.Kind);

        var blank = SteamCmdOutputParser.Parse("   ");
        Harness.Equal("blank line", SteamCmdEventKind.Raw, blank.Kind);

        // A result is only successful when both signals agree.
        Harness.Equal("exit 0 without success line is a failure", false,
            new SteamCmdResult(0, false, null, null).Succeeded);
        Harness.Equal("success line with non-zero exit is a failure", false,
            new SteamCmdResult(3, true, 1u, null).Succeeded);
        Harness.Equal("both signals agree", true,
            new SteamCmdResult(0, true, 1u, null).Succeeded);

        // ---- app_update, i.e. downloading a build ----

        // The progress line carries no percent sign, so before it had a pattern of its
        // own it fell through as Raw and the bar never moved during a download.
        var downloading = SteamCmdOutputParser.Parse(
            " Update state (0x61) downloading, progress: 45.17 (1234567 / 2734567)");
        Harness.Equal("download progress event", SteamCmdEventKind.Downloading, downloading.Kind);
        Harness.Equal("download percent", 45.17d, downloading.Percent);
        Harness.Equal("download state name", "downloading", downloading.Detail);

        var verifying = SteamCmdOutputParser.Parse(
            "[2026-08-21 18:40:02]  Update state (0x5) verifying install, progress: 12.34 (10 / 81)");
        Harness.Equal("stamped update state still detected",
            SteamCmdEventKind.Downloading, verifying.Kind);
        Harness.Equal("multi-word state name", "verifying install", verifying.Detail);

        var reconfiguring = SteamCmdOutputParser.Parse(" Update state (0x3) reconfiguring");
        Harness.Equal("state without progress", SteamCmdEventKind.Downloading, reconfiguring.Kind);
        Harness.Equal("no percent when steamcmd gives none", null, reconfiguring.Percent);

        var installed = SteamCmdOutputParser.Parse("Success! App '480' fully installed.");
        Harness.Equal("download success", SteamCmdEventKind.DownloadSucceeded, installed.Kind);
        Harness.Equal("installed app id", "480", installed.Detail);

        var diskFull = SteamCmdOutputParser.Parse("Error! App '480' state is 0x202 after update job.");
        Harness.Equal("update job failure", SteamCmdEventKind.DownloadFailed, diskFull.Kind);
        Harness.Check("disk space explained",
            diskFull.Detail?.Contains("disk space", StringComparison.OrdinalIgnoreCase) == true,
            diskFull.Detail);

        // Without a pattern of its own this line is claimed by the generic FAILED matcher
        // and reported as a login failure with the reason "to install app".
        var notOwned = SteamCmdOutputParser.Parse("ERROR! Failed to install app '480' (No subscription)");
        Harness.Equal("install refusal", SteamCmdEventKind.DownloadFailed, notOwned.Kind);
        Harness.Check("ownership explained",
            notOwned.Detail?.Contains("does not own", StringComparison.OrdinalIgnoreCase) == true,
            notOwned.Detail);
    }

    private static void Download()
    {
        Console.WriteLine("== build download ==");

        var request = new DownloadRequest("beta", null, Path.Combine(Path.GetTempPath(), "spd-download"));
        var arguments = SteamCmdSession.DownloadArguments(480, "someone", request, null);
        var commandLine = string.Join(' ', arguments);

        // steamcmd honours force_install_dir only before logon; after it, the download
        // lands in steamcmd's own folder with a warning nobody reads.
        Harness.Check("install dir precedes login",
            arguments.IndexOf("+force_install_dir") < arguments.IndexOf("+login"), commandLine);
        Harness.Equal("login account", "someone", arguments[arguments.IndexOf("+login") + 1]);
        Harness.Check("app_update names the branch and validates",
            commandLine.Contains("+app_update 480 -beta beta validate"), commandLine);
        Harness.Equal("ends with quit", "+quit", arguments[^1]);
        Harness.Check("no platform override unless asked",
            !arguments.Contains("+@sSteamCmdForcePlatformType"), commandLine);

        var linux = SteamCmdSession.DownloadArguments(480, "someone",
            request with { Platform = DownloadPlatform.Linux }, null);
        Harness.Equal("platform override comes first", "+@sSteamCmdForcePlatformType", linux[0]);
        Harness.Equal("platform name", "linux", linux[1]);

        // "-beta public" is not how steamcmd spells the default branch; no switch is.
        var publicBranch = SteamCmdSession.DownloadArguments(480, "someone",
            request with { Branch = "public" }, null);
        Harness.Check("default branch has no -beta switch", !publicBranch.Contains("-beta"),
            string.Join(' ', publicBranch));
        Harness.Check("default branch spelled either way",
            SteamCmdSession.IsDefaultBranch("PUBLIC") && SteamCmdSession.IsDefaultBranch("") &&
            !SteamCmdSession.IsDefaultBranch("beta"));

        // A branch password travels in a script file, never on the command line, where
        // the process list would show it to every other program on the machine.
        var scripted = SteamCmdSession.DownloadArguments(480, "someone",
            request with { BranchPassword = "s3cret" }, @"C:\tmp\script.txt");
        Harness.Check("password never on the command line",
            !scripted.Any(a => a.Contains("s3cret")), string.Join(' ', scripted));
        Harness.Check("app_update moves into the script",
            !scripted.Contains("+app_update") && scripted.Contains("+runscript"), string.Join(' ', scripted));
        Harness.Equal("script path follows runscript", @"C:\tmp\script.txt",
            scripted[scripted.IndexOf("+runscript") + 1]);
        Harness.Equal("script line", "app_update 480 -beta beta -betapassword s3cret validate",
            SteamCmdSession.PasswordScript(480, "beta", "s3cret"));
        Harness.Equal("password with spaces is quoted",
            "app_update 480 -beta beta -betapassword \"two words\" validate",
            SteamCmdSession.PasswordScript(480, "beta", "two words"));

        // What refuses to start, before steamcmd is even looked for.
        var contentRoot = Path.Combine(Path.GetTempPath(), "spd-content");
        var profile = new BuildProfile { AppId = 480, SteamAccountName = "someone", ContentRoot = contentRoot };

        Harness.Equal("a sound request has no problem", null, SteamCmdSession.DownloadProblem(profile, request));
        Harness.Check("no account is refused",
            SteamCmdSession.DownloadProblem(new BuildProfile { AppId = 480 }, request) is not null);
        Harness.Check("no app id is refused",
            SteamCmdSession.DownloadProblem(new BuildProfile { SteamAccountName = "someone" }, request) is not null);
        Harness.Check("no branch is refused",
            SteamCmdSession.DownloadProblem(profile, request with { Branch = " " }) is not null);
        Harness.Check("no folder is refused",
            SteamCmdSession.DownloadProblem(profile, request with { InstallDirectory = "" }) is not null);

        // The same mistake the validator refuses for the build output: a download inside
        // the content folder ships with the next upload.
        Harness.Check("download inside the content folder is refused",
            SteamCmdSession.DownloadProblem(profile,
                request with { InstallDirectory = Path.Combine(contentRoot, "downloaded") }) is not null);
        Harness.Check("the content folder itself is refused",
            SteamCmdSession.DownloadProblem(profile, request with { InstallDirectory = contentRoot }) is not null);
        Harness.Equal("a sibling folder is fine", null,
            SteamCmdSession.DownloadProblem(profile,
                request with { InstallDirectory = Path.Combine(Path.GetTempPath(), "spd-content-download") }));
    }

    private static void CiExport()
    {
        Console.WriteLine("== CI export ==");

        var yaml = GitHubActionsExporter.Export(SampleProfile());
        Harness.Check("workflow names the app", yaml.Contains("AppID 480"));
        Harness.Check("uses the config secret", yaml.Contains("STEAM_CONFIG_VDF"));
        Harness.Check("writes the app script", yaml.Contains("scripts/app_build_480.vdf"));
        Harness.Check("runs the build", yaml.Contains("+run_app_build"));
        Harness.Check("no absolute dev paths leak", !yaml.Contains("/games/sample/content"));
        Harness.Check("content root rewritten for CI", yaml.Contains("./content"));

        // The embedded heredoc must itself be valid VDF once un-indented.
        var start = yaml.IndexOf("cat > scripts/app_build_480.vdf <<'VDF'", StringComparison.Ordinal);
        Harness.Check("heredoc present", start >= 0);
        if (start >= 0)
        {
            var body = yaml[(yaml.IndexOf('\n', start) + 1)..];
            var end = body.IndexOf("          VDF", StringComparison.Ordinal);
            var script = string.Join('\n', body[..end]
                .Split('\n')
                .Select(l => l.Length >= 10 ? l[10..] : l));
            var parsed = VdfParser.Parse(script);
            Harness.Equal("embedded script parses", "AppBuild", parsed.Key);
            Harness.Equal("embedded content root", "./content", parsed.GetString("ContentRoot"));
        }
    }

    private static void Locator()
    {
        Console.WriteLine("== steamcmd locator ==");

        var root = Path.Combine(Path.GetTempPath(), "sps-sdk-" + Guid.NewGuid().ToString("N"));
        var builder = Path.Combine(root, "tools", "ContentBuilder", "builder_linux");
        Directory.CreateDirectory(builder);
        File.WriteAllText(Path.Combine(builder, "steamcmd.sh"), "#!/bin/sh\n");

        // Accept the SDK root as well as the ContentBuilder folder itself.
        Harness.Check("locates from sdk root",
            SteamCmdLocator.TryLocate(root, out _, out _) || !OperatingSystem.IsLinux());
        Harness.Check("locates from ContentBuilder folder",
            SteamCmdLocator.TryLocate(Path.Combine(root, "tools", "ContentBuilder"), out _, out _)
            || !OperatingSystem.IsLinux());

        Harness.Check("missing folder reports an error",
            !SteamCmdLocator.TryLocate(Path.Combine(root, "nope"), out _, out var error) &&
            error.Contains("does not exist"));

        Harness.Check("empty path reports an error", !SteamCmdLocator.TryLocate("", out _, out _));

        Directory.Delete(root, true);
    }

    private static void ConsoleLogDeduplication()
    {
        Console.WriteLine("== console log de-duplication ==");

        // Verbatim from a real upload — AppID 2089870, build 24864138. steamcmd's console
        // log carries the run as it happens; the pipe hands the same run over in one block
        // at the end, with partial writes run together. Before the filter the panel
        // printed all of it twice.
        var fromLog = new[]
        {
            "[2026-08-21 16:27:04] Loading Steam API...",
            "[2026-08-21 16:27:04] OK",
            "[2026-08-21 16:27:04] Logging in user 'someone' [U:1:110886633] to Steam Public...",
            "[2026-08-21 16:27:06] OK",
            "[2026-08-21 16:27:06] Waiting for client config...",
            "[2026-08-21 16:27:07] OK",
            "[2026-08-21 16:27:07] [2026-08-21 16:27:07]: Starting AppID 2089870 build (flags 0x0).",
            "[2026-08-21 16:27:07] [2026-08-21 16:27:07]: Building depot 2089871...",
            "[2026-08-21 16:27:07] .",
            "[2026-08-21 16:27:07] .",
            "[2026-08-21 16:27:07]  23.1MB (23%)",
            "[2026-08-21 16:27:42] [2026-08-21 16:27:42]: Successfully finished AppID 2089870 " +
            "build (BuildID 24864138)."
        };

        var fromPipe = new[]
        {
            "Loading Steam API...OK",
            "Logging in user 'someone' [U:1:110886633] to Steam Public...OK",
            "Waiting for client config...OK",
            "[2026-08-21 16:27:07]: Starting AppID 2089870 build (flags 0x0).",
            "[2026-08-21 16:27:07]: Building depot 2089871...",
            ".. 23.1MB (23%)",
            "[2026-08-21 16:27:42]: Successfully finished AppID 2089870 build (BuildID 24864138)."
        };

        var filter = new SteamCmdRunner.DuplicateFilter();
        foreach (var line in fromLog)
            filter.ShouldSuppressLogLine(SteamCmdOutputParser.StripLogTimestamp(line));

        var leaked = fromPipe
            .Where(line => !filter.ShouldSuppressPipeLine(SteamCmdOutputParser.StripLogTimestamp(line)))
            .ToList();

        Harness.Equal("no pipe line escapes the filter", 0, leaked.Count);
        foreach (var line in leaked) Console.WriteLine("        leaked: " + line);

        // The other order, and it happens on every successful upload: at shutdown the
        // pipe flushes the joined line as the process exits, and the log tail delivers
        // the same message in pieces one poll later.
        var atShutdown = new SteamCmdRunner.DuplicateFilter();
        Harness.Equal("the pipe's joined line is shown", false,
            atShutdown.ShouldSuppressPipeLine("Unloading Steam API...OK"));
        Harness.Equal("the log's first piece is dropped", true,
            atShutdown.ShouldSuppressLogLine("Unloading Steam API..."));
        Harness.Equal("the log's second piece is dropped", true,
            atShutdown.ShouldSuppressLogLine("OK"));
        Harness.Equal("and the entry is spent", false,
            atShutdown.ShouldSuppressLogLine("OK"));

        // And the interleaved order, which is what a real run actually does: the log's
        // first half, then the pipe's joined line, then the log's second half.
        var interleaved = new SteamCmdRunner.DuplicateFilter();
        Harness.Equal("the log's first half is shown", false,
            interleaved.ShouldSuppressLogLine("Unloading Steam API..."));
        Harness.Equal("the pipe's joined line is dropped", true,
            interleaved.ShouldSuppressPipeLine("Unloading Steam API...OK"));
        Harness.Equal("the log's second half is still shown", false,
            interleaved.ShouldSuppressLogLine("OK"));

        // The cut-short match only applies at the end of what the log has produced. A
        // pipe line that diverges in the middle of the chain is different output and has
        // to be shown.
        var diverging = new SteamCmdRunner.DuplicateFilter();
        diverging.ShouldSuppressLogLine("Scanning content");
        diverging.ShouldSuppressLogLine("Uploading content...");
        Harness.Equal("a line that breaks the chain is kept", false,
            diverging.ShouldSuppressPipeLine("Scanning content and then something else"));

        // Genuinely new output must survive. A line the log never produced is not a
        // duplicate, however similar it looks.
        var fresh = new SteamCmdRunner.DuplicateFilter();
        fresh.ShouldSuppressLogLine("Uploading content...");
        Harness.Equal("an unseen line is kept", false,
            fresh.ShouldSuppressPipeLine("Scanning content"));
        Harness.Equal("the seen line is claimed", true,
            fresh.ShouldSuppressPipeLine("Uploading content..."));
        Harness.Equal("and only once", false,
            fresh.ShouldSuppressPipeLine("Uploading content..."));

        // Progress repeats the same text constantly; each occurrence has to cancel
        // exactly one, or the second half of an upload stops being displayed.
        var repeated = new SteamCmdRunner.DuplicateFilter();
        repeated.ShouldSuppressLogLine(".");
        repeated.ShouldSuppressLogLine(".");
        Harness.Equal("first repeat claimed", true, repeated.ShouldSuppressPipeLine("."));
        Harness.Equal("second repeat claimed", true, repeated.ShouldSuppressPipeLine("."));
        Harness.Equal("third is a new line", false, repeated.ShouldSuppressPipeLine("."));
    }

    private static void Secrets()
    {
        Console.WriteLine("== secret store ==");

        Harness.Equal("password name is per account",
            "steam-password-andreagalet332", SecretStoreFactory.SteamPassword("andreagalet332"));

        Harness.Equal("account casing does not fork the entry",
            SecretStoreFactory.SteamPassword("AndreaGalet332"),
            SecretStoreFactory.SteamPassword("andreagalet332"));

        Harness.Equal("surrounding space is not part of the identity",
            SecretStoreFactory.SteamPassword("andreagalet332"),
            SecretStoreFactory.SteamPassword("  andreagalet332 "));

        // The name becomes a file name. A separator in the account field must not be
        // able to steer the write out of the secrets folder.
        var hostile = SecretStoreFactory.SteamPassword("../../etc/passwd");
        Harness.Check("path separators are neutralised",
            !hostile.Contains('/') && !hostile.Contains('\\') && !hostile.Contains(".."),
            hostile);
        Harness.Check("every name stays inside the namespace",
            hostile.StartsWith("steam-password-", StringComparison.Ordinal), hostile);

        var root = Path.Combine(Path.GetTempPath(), "sps-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var store = SecretStoreFactory.Create(root);
            var name = SecretStoreFactory.SteamPassword("testaccount");

            Harness.Equal("unset secret reads as null", null, store.Read(name));

            store.Write(name, "hunter2 with spaces and ünicode");
            Harness.Equal("secret round-trips", "hunter2 with spaces and ünicode", store.Read(name));

            store.Write(name, "replaced");
            Harness.Equal("writing again replaces", "replaced", store.Read(name));

            store.Delete(name);
            Harness.Equal("deleted secret reads as null", null, store.Read(name));

            // Deleting something that was never there is what "Remove" does on a fresh
            // profile, and it must not throw at the user.
            store.Delete(name);
            Harness.Check("deleting twice is harmless", true);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
