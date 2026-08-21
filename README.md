# SteamPipe Studio

A modern replacement for `SteamPipeGUI.exe`, the build uploader that ships in
`sdk/tools/SteamPipeGUI` of the Steamworks SDK.

The original is a .NET Framework 4.5 WinForms app, x86-only, version 1.4.0.2, last
touched around 2018. It fills in some fields, writes a `.vdf`, shells out to `steamcmd`
and tails the log. SteamPipe Studio does that on .NET 10 and Avalonia — Windows, macOS
and Linux — and adds the half the original never had: knowing what is already live on
Steam.

Not affiliated with or endorsed by Valve.

<!-- Add a screenshot of the Upload tab here once you have one: it is the screen that
     sells the tool, and a GUI project without a screenshot reads as abandoned. -->

## What it does

- **Shows you the build before you upload it.** *Preview contents* resolves every file
  mapping against the real filesystem and reports the file count, total size and largest
  files, plus warnings for the two mistakes that cost the most time: a mapping that
  matched nothing, and debug symbols about to ship to customers.
- **Refuses the errors that waste an hour.** Validation runs before `steamcmd` starts:
  build output nested inside the content root (so the chunk cache uploads itself into
  your game), duplicate depot IDs, an empty content folder, an absolute depot path,
  `SetLive` pointing at the default branch — which Steam rejects silently, fifteen
  minutes in.
- **Never stores your password.** `steamcmd` is always launched as `+login <account>`,
  so it reuses the session token it caches itself and prompts on stdin only when that
  token has expired. Nothing is written to disk and nothing appears in the process list.
  The original offered a "save password" checkbox and wrote it in clear text into
  `user.config`.
- **Reads the scripts you already have.** *Import app_build.vdf* parses an existing
  script, follows its depot references and turns it into an editable project. Both SDK
  layouts round-trip: the inline depot of `simple_app_build.vdf` and the multi-file
  `app_build_1000.vdf`.
- **Tells you what is live.** The Builds tab reads build history and branches through
  the Steamworks partner Web API and can promote a build to a branch, which is the
  reason you would otherwise keep the Steamworks site open in a tab.
- **Exports itself to CI.** *Export GitHub Actions workflow* writes a workflow that
  performs the same upload with the paths rewritten for a runner. A tool that can only
  be driven by clicking is a tool a team eventually has to replace.
- **Runs anywhere `steamcmd` does.** The Windows-only limitation of the original was
  never inherent: which builder you run has nothing to do with which platform you ship.

## Requirements

- The [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A copy of the [Steamworks SDK](https://partner.steamgames.com/doc/sdk) — the app needs
  the `sdk/tools/ContentBuilder` folder from it
- A Steam account with upload rights for the app you are publishing

## Running it

```bash
git clone https://github.com/<your-org>/steampipe-studio.git
cd steampipe-studio
dotnet restore
dotnet run --project src/SteamPipeStudio.App
```

Then open **Settings** and point *Steamworks SDK* at your `sdk/tools/ContentBuilder`
folder. When the path is right, the line underneath reports where it found `steamcmd`.

Building a standalone binary:

```bash
dotnet publish src/SteamPipeStudio.App -c Release -r win-x64 \
  -p:PublishSingleFile=true --self-contained false
```

`osx-arm64`, `osx-x64` and `linux-x64` work the same way. Use `--self-contained true`
for a build that does not need .NET installed, at the cost of roughly 70 MB.

## Tests

```bash
dotnet run --project src/SteamPipeStudio.Tests
```

113 assertions over the whole Core library, exiting non-zero on failure. No test
framework and no packages: the suite runs on a locked-down build agent that cannot
restore from NuGet, which is exactly where you want a build pipeline to still work.

## Layout

```
src/SteamPipeStudio.Core/     no UI dependencies, no NuGet packages at all
├── Vdf/                      KeyValues parser and writer
├── Model/                    profiles, settings, JSON persistence
├── Build/                    script generation, validation, content preflight
├── Steam/                    steamcmd process control, partner Web API
├── Security/                 per-platform secret storage
└── Ci/                       GitHub Actions export

src/SteamPipeStudio.App/      Avalonia 11, MVVM, the only project with packages
src/SteamPipeStudio.Tests/    zero-dependency test harness
```

`TargetFramework` and `$(AvaloniaVersion)` live in `Directory.Build.props` at the root,
so retargeting or bumping Avalonia is one line rather than three.

Keeping Core free of UI dependencies is deliberate: the whole build pipeline — parsing,
generating, validating, running `steamcmd` — can be reused from a CLI, a test harness or
a CI task without dragging a desktop framework along.

## Notes on the tricky parts

**The VDF parser does not process escape sequences, on purpose.** Valve's own scripts
contain `"ContentRoot" "..\content\"`. Under standard escaping the trailing `\"` is an
escaped quote and the file will not parse. `steamcmd` reads build scripts with escapes
off, so this matches it. Generated scripts use forward slashes on every platform, which
sidesteps the ambiguity entirely.

**Keys are neither unique nor case-sensitive.** A depot script legitimately holds several
`FileMapping` blocks and several `FileExclusion` values, and the SDK samples mix
`"Recursive"` with `"recursive"` in the same file. Children are an ordered list and
lookup is case-insensitive; a dictionary-backed parser silently discards mappings.

**Exclusion wildcards cross directories; mapping wildcards do not.** `*.pdb` removes
symbols at every depth and `bin/tools*` removes a subtree, but `bin/*` stays at one level
unless `Recursive` is set. They look like the same glob and are not.

**The output is read character by character, not line by line.** `steamcmd` rewrites
progress in place with a bare carriage return and no newline. `ReadLineAsync` appears to
hang for minutes during a large upload and then dumps everything at once — and an
unterminated `Steam Guard code:` prompt is never seen at all, so the process deadlocks
waiting for input nobody knows it wants.

**Exit code 0 is not success.** `steamcmd` has returned 0 after a failed depot commit. A
run counts as successful only when the "Successfully finished appID … - build …" line was
also seen.

## Known limits

Two things cannot be verified without a real publisher account, and are the first places
to look if something misbehaves:

- **`Steam/SteamCmdOutputParser.cs`** — Valve does not document `steamcmd`'s output
  format and it changes between SDK releases. Anything unrecognised falls through to the
  log verbatim, so a mismatch degrades to "the progress bar stops moving", not a crash.
  If a successful build stops being detected after an SDK update, this file is the only
  thing that needs changing.
- **`Steam/PartnerApiClient.cs`** — the JSON shapes of `GetAppBuilds` and `GetAppBetas`
  are not published. Parsing walks the response looking for the fields it needs rather
  than binding to a fixed schema, so a reshaped response degrades to missing columns
  rather than an exception.

Not built yet: per-depot platform gating (`[$WIN32]` conditionals survive an import but
are not editable), drag-and-drop onto the content-root field, diffing a build against the
previous one, and macOS notarisation through the SDK's `ContentPrep.app`.

## Contributing

The project is free and stays free. Use it, change it, ship your own version — the
license below spells out the terms.

If you want your change in the upstream project:

1. **Fork the repository.** Every contribution arrives as a pull request from a fork;
   nobody pushes to this repo directly.
2. **Open an issue first for anything larger than a bug fix.** It is cheaper for both of
   us to disagree about an approach before you have written it.
3. **Keep `dotnet run --project src/SteamPipeStudio.Tests` green,** and add assertions
   for what you changed. If your change touches VDF parsing, file matching or validation,
   a test that fails without your fix is the point of the pull request.
4. **Put logic in Core, not in a view model.** Core has no UI dependencies and that is
   what makes it testable; anything that could be tested headlessly belongs there.
5. **Explain the non-obvious in a comment.** Most of the awkward code in this project
   exists because `steamcmd` or the VDF format is awkward. A comment saying *why*
   survives the next refactor; one restating *what* does not.

Bug reports are welcome and useful. Include the `steamcmd` build log from your build
output folder, and say which SDK version you are on.

## Donations

SteamPipe Studio is free software and will stay that way — there is no paid tier, no
license key and nothing withheld. If it saved you an afternoon and you want to say
thanks, donations are welcome.

<!-- TODO: replace with your donation link once you have picked a platform.
     GitHub Sponsors also needs .github/FUNDING.yml for the Sponsor button to appear. -->

*Donation link coming soon.*

Contributing code, reporting a bug precisely, or improving the documentation helps just
as much, and costs nothing.

## License

GNU General Public License v3.0 — see [LICENSE](LICENSE).

In short: you may use, study, modify and redistribute this program freely. If you
distribute a modified version, you must release its source under the same license, so
that everyone who receives your version keeps the same freedoms. That is a deliberate
choice: improvements to a shipping tool should stay available to the people shipping
with it.

```
Copyright (C) 2026  <your name or studio>

This program is free software: you can redistribute it and/or modify it under the
terms of the GNU General Public License as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE.  See the GNU General Public License for more details.

You should have received a copy of the GNU General Public License along with this
program.  If not, see <https://www.gnu.org/licenses/>.
```
