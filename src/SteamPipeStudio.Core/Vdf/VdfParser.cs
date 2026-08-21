using System;
using System.Collections.Generic;
using System.Text;

namespace SteamPipeStudio.Core.Vdf;

public sealed class VdfParseException : Exception
{
    public VdfParseException(string message, int line, int column)
        : base($"{message} (line {line}, column {column})")
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }
    public int Column { get; }
}

/// <summary>
/// Recursive-descent parser for Valve KeyValues text.
///
/// Escape sequences are DISABLED by default, and that is deliberate. Valve's own
/// build scripts contain lines such as:
///
///     "ContentRoot" "..\content\"
///
/// Under standard escaping the trailing <c>\"</c> would be an escaped quote and the
/// document would fail to parse. steamcmd reads these files with escape processing
/// off, so we match that behaviour. Pass <c>allowEscapeSequences: true</c> only when
/// reading a file that is known to use them.
/// </summary>
public static class VdfParser
{
    public static VdfNode ParseFile(string path, bool allowEscapeSequences = false)
        => Parse(System.IO.File.ReadAllText(path), allowEscapeSequences);

    /// <summary>
    /// Parses a document and returns its single root node (e.g. <c>AppBuild</c>).
    /// </summary>
    public static VdfNode Parse(string text, bool allowEscapeSequences = false)
    {
        var roots = ParseAll(text, allowEscapeSequences);
        if (roots.Count == 0)
            throw new VdfParseException("Document is empty", 1, 1);
        return roots[0];
    }

    /// <summary>Parses a document that may contain several top-level nodes.</summary>
    public static List<VdfNode> ParseAll(string text, bool allowEscapeSequences = false)
    {
        var lexer = new Lexer(text, allowEscapeSequences);
        var roots = new List<VdfNode>();

        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.End) break;
            if (token.Kind != TokenKind.String)
                throw new VdfParseException($"Expected a key, found '{token.Text}'", token.Line, token.Column);

            roots.Add(ParseNode(lexer, token));
        }

        return roots;
    }

    private static VdfNode ParseNode(Lexer lexer, Token keyToken)
    {
        // A comment and/or a conditional may sit between the key and what follows it.
        // The SDK's own simple_app_build.vdf does exactly this:
        //
        //     "1001" // your DepotID
        //     {
        //
        // so anything that treats the token after a key as necessarily the value or the
        // opening brace fails on Valve's shipped samples.
        string? comment = null;
        string? condition = null;
        Token next;

        while (true)
        {
            next = lexer.Next();

            if (next.Kind == TokenKind.Comment) { comment ??= next.Text; continue; }
            if (next.Kind == TokenKind.Condition) { condition ??= next.Text; continue; }
            break;
        }

        switch (next.Kind)
        {
            case TokenKind.String:
            {
                var leaf = VdfNode.Leaf(keyToken.Text, next.Text);
                leaf.Comment = comment;
                leaf.Condition = condition;
                AttachTrailers(lexer, leaf, next.Line);
                return leaf;
            }

            case TokenKind.OpenBrace:
            {
                var block = VdfNode.Block(keyToken.Text);
                block.Comment = comment;
                block.Condition = condition;
                ParseBlockBody(lexer, block);
                return block;
            }

            default:
                throw new VdfParseException(
                    $"Expected a value or '{{' after key '{keyToken.Text}'", next.Line, next.Column);
        }
    }

    private static void ParseBlockBody(Lexer lexer, VdfNode block)
    {
        while (true)
        {
            var token = lexer.Next();
            switch (token.Kind)
            {
                case TokenKind.CloseBrace:
                    return;
                case TokenKind.End:
                    throw new VdfParseException($"Unterminated block '{block.Key}'", token.Line, token.Column);
                case TokenKind.String:
                    block.Add(ParseNode(lexer, token));
                    break;
                case TokenKind.Comment:
                    break; // standalone comments inside blocks are dropped
                default:
                    throw new VdfParseException(
                        $"Unexpected '{token.Text}' inside block '{block.Key}'", token.Line, token.Column);
            }
        }
    }

    /// <summary>Attaches a same-line trailing comment and/or conditional to a leaf node.</summary>
    private static void AttachTrailers(Lexer lexer, VdfNode leaf, int valueLine)
    {
        while (true)
        {
            var peek = lexer.Peek();
            if (peek.Line != valueLine) return;

            if (peek.Kind == TokenKind.Comment)
            {
                leaf.Comment = peek.Text;
                lexer.Next();
                continue;
            }
            if (peek.Kind == TokenKind.Condition && leaf.Condition is null)
            {
                leaf.Condition = peek.Text;
                lexer.Next();
                continue;
            }
            return;
        }
    }

    // ------------------------------------------------------------------
    // Lexer
    // ------------------------------------------------------------------

    private enum TokenKind { String, OpenBrace, CloseBrace, Condition, Comment, End }

    private readonly struct Token
    {
        public Token(TokenKind kind, string text, int line, int column)
        {
            Kind = kind; Text = text; Line = line; Column = column;
        }

        public TokenKind Kind { get; }
        public string Text { get; }
        public int Line { get; }
        public int Column { get; }
    }

    private sealed class Lexer
    {
        private readonly string _src;
        private readonly bool _escapes;
        private int _pos;
        private int _line = 1;
        private int _col = 1;
        private Token? _peeked;

        public Lexer(string src, bool escapes)
        {
            // Strip a UTF-8 BOM: some SDK scripts ship with one, and U+FEFF would
            // otherwise be lexed as part of the first key.
            _src = src.Length > 0 && src[0] == '\uFEFF' ? src[1..] : src;
            _escapes = escapes;
        }

        public Token Peek() => _peeked ??= Read();

        public Token Next()
        {
            if (_peeked is { } t) { _peeked = null; return t; }
            return Read();
        }

        private Token Read()
        {
            SkipWhitespace();
            if (_pos >= _src.Length) return new Token(TokenKind.End, "<eof>", _line, _col);

            var startLine = _line;
            var startCol = _col;
            var c = _src[_pos];

            switch (c)
            {
                case '{':
                    Advance();
                    return new Token(TokenKind.OpenBrace, "{", startLine, startCol);
                case '}':
                    Advance();
                    return new Token(TokenKind.CloseBrace, "}", startLine, startCol);
                case '"':
                    return ReadQuoted(startLine, startCol);
                case '[':
                    return ReadCondition(startLine, startCol);
                case '/' when _pos + 1 < _src.Length && _src[_pos + 1] == '/':
                    return ReadComment(startLine, startCol);
                default:
                    return ReadBare(startLine, startCol);
            }
        }

        private void SkipWhitespace()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos]))
                Advance();
        }

        private void Advance()
        {
            if (_src[_pos] == '\n') { _line++; _col = 1; }
            else { _col++; }
            _pos++;
        }

        private Token ReadQuoted(int line, int col)
        {
            Advance(); // opening quote
            var sb = new StringBuilder();

            while (_pos < _src.Length)
            {
                var c = _src[_pos];

                if (c == '"')
                {
                    Advance();
                    return new Token(TokenKind.String, sb.ToString(), line, col);
                }

                if (c == '\\' && _escapes && _pos + 1 < _src.Length)
                {
                    Advance();
                    var esc = _src[_pos];
                    sb.Append(esc switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        _ => esc
                    });
                    Advance();
                    continue;
                }

                if (c == '\n')
                    throw new VdfParseException("Unterminated quoted string", line, col);

                sb.Append(c);
                Advance();
            }

            throw new VdfParseException("Unterminated quoted string", line, col);
        }

        private Token ReadBare(int line, int col)
        {
            var sb = new StringBuilder();
            while (_pos < _src.Length)
            {
                var c = _src[_pos];
                if (char.IsWhiteSpace(c) || c is '{' or '}' or '"' or '[') break;
                if (c == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '/') break;
                sb.Append(c);
                Advance();
            }

            if (sb.Length == 0)
                throw new VdfParseException("Unexpected character in document", line, col);

            return new Token(TokenKind.String, sb.ToString(), line, col);
        }

        private Token ReadCondition(int line, int col)
        {
            var sb = new StringBuilder();
            while (_pos < _src.Length)
            {
                var c = _src[_pos];
                sb.Append(c);
                Advance();
                if (c == ']')
                    return new Token(TokenKind.Condition, sb.ToString(), line, col);
                if (c == '\n')
                    break;
            }
            throw new VdfParseException("Unterminated conditional", line, col);
        }

        private Token ReadComment(int line, int col)
        {
            Advance(); Advance(); // "//"
            var sb = new StringBuilder();
            while (_pos < _src.Length && _src[_pos] != '\n')
            {
                sb.Append(_src[_pos]);
                Advance();
            }
            return new Token(TokenKind.Comment, sb.ToString().Trim(), line, col);
        }
    }
}
