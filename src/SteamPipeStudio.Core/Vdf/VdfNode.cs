using System;
using System.Collections.Generic;
using System.Linq;

namespace SteamPipeStudio.Core.Vdf;

/// <summary>
/// A node in a Valve KeyValues (VDF) document.
///
/// Two important properties of the real format that naive implementations get wrong:
///
///  1. Keys are NOT unique. A depot script legitimately contains several "FileMapping"
///     blocks and several "FileExclusion" values. Therefore children are an ordered
///     <see cref="List{T}"/>, never a dictionary.
///  2. Keys are case-insensitive. Valve's own SDK samples mix "Recursive"/"recursive"
///     and "Preview"/"preview" inside the same file.
/// </summary>
public sealed class VdfNode
{
    private readonly List<VdfNode> _children = new();

    private VdfNode(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }

    /// <summary>The key as written in the source document (original casing preserved).</summary>
    public string Key { get; set; }

    /// <summary>
    /// Scalar value, or <c>null</c> when this node is a block. A node is never both.
    /// </summary>
    public string? Value { get; private set; }

    /// <summary>A trailing <c>// comment</c> that followed this node on the same line.</summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Conditional suffix such as <c>[$WIN32]</c>, preserved verbatim so round-tripping
    /// an existing file does not silently drop platform gating.
    /// </summary>
    public string? Condition { get; set; }

    public bool IsBlock => Value is null;

    public IReadOnlyList<VdfNode> Children => _children;

    public static VdfNode Block(string key) => new(key);

    public static VdfNode Leaf(string key, string value) => new(key) { Value = value };

    public VdfNode Add(VdfNode child)
    {
        if (!IsBlock)
            throw new InvalidOperationException(
                $"Cannot add a child to '{Key}': it holds the scalar value '{Value}'.");
        _children.Add(child);
        return this;
    }

    public VdfNode Add(string key, string value) => Add(Leaf(key, value));

    public VdfNode AddBlock(string key)
    {
        var block = Block(key);
        Add(block);
        return block;
    }

    public bool Remove(VdfNode child) => _children.Remove(child);

    public void Clear() => _children.Clear();

    // ---- lookup helpers (all case-insensitive, matching Valve's parser) ----

    public VdfNode? Find(string key) =>
        _children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<VdfNode> FindAll(string key) =>
        _children.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? GetString(string key) => Find(key)?.Value;

    public string GetString(string key, string fallback) => Find(key)?.Value ?? fallback;

    public bool GetBool(string key, bool fallback = false)
    {
        var raw = GetString(key);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        // Valve writes flags as "0"/"1"; tolerate true/false for hand-edited files.
        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            var s when s.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            var s when s.Equals("false", StringComparison.OrdinalIgnoreCase) => false,
            _ => fallback
        };
    }

    public uint GetUInt(string key, uint fallback = 0) =>
        uint.TryParse(GetString(key), out var v) ? v : fallback;

    /// <summary>Sets a scalar child, replacing the first existing one with that key.</summary>
    public void SetString(string key, string value)
    {
        var existing = Find(key);
        if (existing is { IsBlock: false })
        {
            existing.Value = value;
            return;
        }
        Add(key, value);
    }

    public void SetBool(string key, bool value) => SetString(key, value ? "1" : "0");

    public override string ToString() => IsBlock ? $"{Key} {{ {_children.Count} }}" : $"{Key} = {Value}";
}
