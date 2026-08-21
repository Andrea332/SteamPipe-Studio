using System;
using System.Globalization;
using System.Text;

namespace SteamPipeStudio.Core.Vdf;

/// <summary>
/// Serialises a <see cref="VdfNode"/> tree back to Valve KeyValues text, matching the
/// tab-indented layout used by the scripts shipped in the Steamworks SDK.
/// </summary>
public static class VdfWriter
{
    /// <summary>
    /// Writes the document. Values are emitted verbatim, without backslash escaping,
    /// because steamcmd parses build scripts with escape sequences turned off — see
    /// <see cref="VdfParser"/>. A value containing a double quote therefore cannot be
    /// represented and is rejected rather than silently corrupting the file.
    /// </summary>
    public static string Write(VdfNode root)
    {
        var sb = new StringBuilder();
        WriteNode(sb, root, 0);
        return sb.ToString();
    }

    public static void WriteFile(string path, VdfNode root)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            System.IO.Directory.CreateDirectory(directory);

        // No BOM: steamcmd's parser treats a leading U+FEFF as part of the first key.
        System.IO.File.WriteAllText(path, Write(root), new UTF8Encoding(false));
    }

    private static void WriteNode(StringBuilder sb, VdfNode node, int depth)
    {
        var indent = new string('\t', depth);

        if (node.IsBlock)
        {
            sb.Append(indent).Append(Quote(node.Key));
            if (node.Condition is { Length: > 0 }) sb.Append(' ').Append(node.Condition);
            if (node.Comment is { Length: > 0 }) sb.Append(" // ").Append(node.Comment);
            sb.Append('\n');
            sb.Append(indent).Append("{\n");

            foreach (var child in node.Children)
                WriteNode(sb, child, depth + 1);

            sb.Append(indent).Append("}\n");
            return;
        }

        sb.Append(indent)
          .Append(Quote(node.Key))
          .Append('\t')
          .Append(Quote(node.Value ?? string.Empty));

        if (node.Condition is { Length: > 0 }) sb.Append(' ').Append(node.Condition);
        if (node.Comment is { Length: > 0 }) sb.Append(" // ").Append(node.Comment);
        sb.Append('\n');
    }

    private static string Quote(string raw)
    {
        if (raw.Contains('"'))
            throw new InvalidOperationException(
                $"A double quote cannot be written to a SteamPipe build script: \"{raw}\". " +
                "steamcmd parses these files without escape sequences, so the value would " +
                "terminate the string early.");

        if (raw.Contains('\n') || raw.Contains('\r'))
            throw new InvalidOperationException(
                "A line break cannot be written to a SteamPipe build script value.");

        return string.Create(raw.Length + 2, raw, static (span, value) =>
        {
            span[0] = '"';
            value.AsSpan().CopyTo(span[1..]);
            span[^1] = '"';
        });
    }

    /// <summary>
    /// Normalises a filesystem path for use inside a build script.
    ///
    /// Forward slashes are used on every platform. steamcmd accepts them on Windows,
    /// and it removes the ambiguity around a trailing backslash sitting directly
    /// before the closing quote (<c>"..\content\"</c>), which is the single most
    /// common way a hand-written build script ends up unparseable.
    /// </summary>
    public static string NormalisePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.Trim().Replace('\\', '/');
    }

    public static string Bool(bool value) => value ? "1" : "0";

    public static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
}
