using Culebral.Compiler.Diagnostics;

namespace Culebral.Compiler.Lexer;

/// <summary>
/// Indentation-aware tokenizer for Culebral source code.
/// Produces INDENT/DEDENT tokens matching Python's off-side rule.
/// Single-pass, streaming design — yields tokens as they are scanned.
/// </summary>
public sealed class CulebralLexer
{
    private readonly string _source;
    private readonly string _filePath;
    private readonly DiagnosticBag _diagnostics;

    private int _pos;
    private int _line = 1;
    private int _col = 1;

    private readonly Stack<int> _indentStack = new();
    private readonly Queue<Token> _pendingTokens = new();
    private bool _atLineStart = true;
    private int _parenDepth; // Tracks (), [], {} nesting — suppresses NEWLINE/INDENT inside

    private static readonly Dictionary<string, TokenKind> Keywords = new()
    {
        ["def"] = TokenKind.KwDef,
        ["class"] = TokenKind.KwClass,
        ["struct"] = TokenKind.KwStruct,
        ["record"] = TokenKind.KwRecord,
        ["enum"] = TokenKind.KwEnum,
        ["interface"] = TokenKind.KwInterface,
        ["prop"] = TokenKind.KwProp,
        ["if"] = TokenKind.KwIf,
        ["elif"] = TokenKind.KwElif,
        ["else"] = TokenKind.KwElse,
        ["for"] = TokenKind.KwFor,
        ["while"] = TokenKind.KwWhile,
        ["in"] = TokenKind.KwIn,
        ["return"] = TokenKind.KwReturn,
        ["yield"] = TokenKind.KwYield,
        ["break"] = TokenKind.KwBreak,
        ["continue"] = TokenKind.KwContinue,
        ["pass"] = TokenKind.KwPass,
        ["import"] = TokenKind.KwImport,
        ["from"] = TokenKind.KwFrom,
        ["as"] = TokenKind.KwAs,
        ["with"] = TokenKind.KwWith,
        ["async"] = TokenKind.KwAsync,
        ["await"] = TokenKind.KwAwait,
        ["match"] = TokenKind.KwMatch,
        ["case"] = TokenKind.KwCase,
        ["and"] = TokenKind.KwAnd,
        ["or"] = TokenKind.KwOr,
        ["not"] = TokenKind.KwNot,
        ["is"] = TokenKind.KwIs,
        ["lambda"] = TokenKind.KwLambda,
        ["when"] = TokenKind.KwWhen,
        ["target"] = TokenKind.KwTarget,
        ["get"] = TokenKind.KwGet,
        ["set"] = TokenKind.KwSet,
        ["via"] = TokenKind.KwVia,
        ["module"] = TokenKind.KwModule,
        ["type"] = TokenKind.KwType,
        ["try"] = TokenKind.KwTry,
        ["except"] = TokenKind.KwExcept,
        ["finally"] = TokenKind.KwFinally,
        ["raise"] = TokenKind.KwRaise,
        ["assert"] = TokenKind.KwAssert,
        ["True"] = TokenKind.BoolLiteral,
        ["False"] = TokenKind.BoolLiteral,
        ["None"] = TokenKind.NoneLiteral,
    };

    public CulebralLexer(string source, string filePath, DiagnosticBag diagnostics)
    {
        _source = source;
        _filePath = filePath;
        _diagnostics = diagnostics;
        _indentStack.Push(0); // Base indentation level
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == TokenKind.EndOfFile)
                break;
        }

        return tokens;
    }

    public Token NextToken()
    {
        // Drain any queued INDENT/DEDENT/NEWLINE tokens first
        if (_pendingTokens.Count > 0)
            return _pendingTokens.Dequeue();

        // Handle indentation at the start of a logical line
        if (_atLineStart && _parenDepth == 0)
        {
            ProcessIndentation();
            if (_pendingTokens.Count > 0)
                return _pendingTokens.Dequeue();
        }

        // Skip inline whitespace (not newlines)
        SkipSpaces();

        if (_pos >= _source.Length)
        {
            // Emit remaining DEDENTs at EOF
            EmitDedentsToLevel(0);
            _pendingTokens.Enqueue(MakeToken(TokenKind.EndOfFile, "", SourceSpan.From(CurrentLocation())));
            return _pendingTokens.Dequeue();
        }

        var ch = Current;

        // Comments
        if (ch == '#')
        {
            SkipComment();
            // After comment, we might be at a newline or EOF
            return NextToken();
        }

        // Newlines
        if (ch == '\n' || (ch == '\r' && Peek(1) == '\n'))
        {
            return ScanNewline();
        }

        if (ch == '\r')
        {
            Advance();
            return NextToken();
        }

        _atLineStart = false;

        // String literals
        if (ch == '"' || ch == '\'')
            return ScanString();

        if (ch == 'f' && _pos + 1 < _source.Length && (_source[_pos + 1] == '"' || _source[_pos + 1] == '\''))
            return ScanFString();

        // Numbers
        if (char.IsDigit(ch) || (ch == '.' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1])))
            return ScanNumber();

        // Identifiers and keywords
        if (ch == '_' || char.IsLetter(ch))
            return ScanIdentifierOrKeyword();

        // @ prefix (field access or decorator)
        if (ch == '@')
            return ScanAtPrefix();

        // Operators and delimiters
        return ScanOperatorOrDelimiter();
    }

    // ─── Indentation Processing ───

    private void ProcessIndentation()
    {
        _atLineStart = false;
        var indent = 0;
        var loc = CurrentLocation();

        while (_pos < _source.Length)
        {
            if (Current == ' ')
            {
                indent++;
                Advance();
            }
            else if (Current == '\t')
            {
                // Tabs are 4 spaces — consistent with Python's recommendation
                indent += 4;
                Advance();
            }
            else
            {
                break;
            }
        }

        // Blank line or comment-only line — skip, don't emit INDENT/DEDENT
        if (_pos >= _source.Length || Current == '\n' || Current == '\r' || Current == '#')
            return;

        var currentIndent = _indentStack.Peek();

        if (indent > currentIndent)
        {
            _indentStack.Push(indent);
            _pendingTokens.Enqueue(MakeToken(TokenKind.Indent, "<INDENT>", SourceSpan.From(loc)));
        }
        else if (indent < currentIndent)
        {
            EmitDedentsToLevel(indent);
            if (_indentStack.Peek() != indent)
            {
                _diagnostics.Error("LEB0001", $"Inconsistent indentation: expected {_indentStack.Peek()} spaces, got {indent}",
                    SourceSpan.From(loc));
            }
        }
    }

    private void EmitDedentsToLevel(int targetIndent)
    {
        var loc = CurrentLocation();
        while (_indentStack.Count > 1 && _indentStack.Peek() > targetIndent)
        {
            _indentStack.Pop();
            _pendingTokens.Enqueue(MakeToken(TokenKind.Dedent, "<DEDENT>", SourceSpan.From(loc)));
        }
    }

    // ─── Scanning Methods ───

    private Token ScanNewline()
    {
        var loc = CurrentLocation();
        if (Current == '\r') Advance();
        if (_pos < _source.Length && Current == '\n') Advance();

        _atLineStart = true;

        // Suppress newlines inside brackets
        if (_parenDepth > 0)
            return NextToken();

        return MakeToken(TokenKind.Newline, "\\n", SourceSpan.From(loc));
    }

    private Token ScanString()
    {
        var start = CurrentLocation();
        var quote = Current;
        Advance();

        // Check for triple-quoted string
        var isTriple = false;
        if (_pos + 1 < _source.Length && Current == quote && _source[_pos + 1] == quote)
        {
            isTriple = true;
            Advance();
            Advance();
        }

        var sb = new System.Text.StringBuilder();

        while (_pos < _source.Length)
        {
            if (isTriple)
            {
                if (Current == quote && _pos + 2 < _source.Length && _source[_pos + 1] == quote && _source[_pos + 2] == quote)
                {
                    Advance(); Advance(); Advance();
                    return MakeToken(TokenKind.StringLiteral, sb.ToString(), new SourceSpan(start, CurrentLocation()));
                }
            }
            else if (Current == quote)
            {
                Advance();
                return MakeToken(TokenKind.StringLiteral, sb.ToString(), new SourceSpan(start, CurrentLocation()));
            }

            if (Current == '\\')
            {
                Advance();
                if (_pos < _source.Length)
                {
                    if (Current == 'x' && _pos + 2 < _source.Length)
                    {
                        // \xNN — 2 hex digits
                        Advance();
                        var hex = _source.Substring(_pos, Math.Min(2, _source.Length - _pos));
                        if (hex.Length == 2 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var hexVal))
                        {
                            sb.Append((char)hexVal);
                            Advance();
                            Advance();
                        }
                        else
                        {
                            sb.Append('\\');
                            sb.Append('x');
                        }
                    }
                    else if (Current == 'u' && _pos + 4 < _source.Length)
                    {
                        // \uNNNN — 4 hex digits
                        Advance();
                        var hex = _source.Substring(_pos, Math.Min(4, _source.Length - _pos));
                        if (hex.Length == 4 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var hexVal))
                        {
                            sb.Append((char)hexVal);
                            Advance(); Advance(); Advance(); Advance();
                        }
                        else
                        {
                            sb.Append('\\');
                            sb.Append('u');
                        }
                    }
                    else
                    {
                        sb.Append(Current switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '\\' => '\\',
                            '\'' => '\'',
                            '"' => '"',
                            '0' => '\0',
                            'a' => '\a',
                            'b' => '\b',
                            'f' => '\f',
                            'v' => '\v',
                            _ => Current,
                        });
                        Advance();
                    }
                }
            }
            else if (!isTriple && (Current == '\n' || Current == '\r'))
            {
                _diagnostics.Error("LEB0002", "Unterminated string literal", new SourceSpan(start, CurrentLocation()));
                break;
            }
            else
            {
                sb.Append(Current);
                Advance();
            }
        }

        if (_pos >= _source.Length)
        {
            _diagnostics.Error("LEB0002", "Unterminated string literal", new SourceSpan(start, CurrentLocation()));
        }

        return MakeToken(TokenKind.StringLiteral, sb.ToString(), new SourceSpan(start, CurrentLocation()));
    }

    private Token ScanFString()
    {
        var start = CurrentLocation();
        Advance(); // skip 'f'
        var quote = Current;
        Advance(); // skip opening quote

        var sb = new System.Text.StringBuilder();

        while (_pos < _source.Length && Current != quote)
        {
            if (Current == '\\')
            {
                Advance();
                if (_pos < _source.Length)
                {
                    sb.Append(Current switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '\'' => '\'',
                        '"' => '"',
                        '{' => '{',
                        '}' => '}',
                        _ => Current,
                    });
                    Advance();
                }
            }
            else if (Current == '\n' || Current == '\r')
            {
                _diagnostics.Error("LEB0002", "Unterminated f-string literal", new SourceSpan(start, CurrentLocation()));
                break;
            }
            else
            {
                sb.Append(Current);
                Advance();
            }
        }

        if (_pos < _source.Length && Current == quote)
            Advance();
        else
            _diagnostics.Error("LEB0002", "Unterminated f-string literal", new SourceSpan(start, CurrentLocation()));

        return MakeToken(TokenKind.FStringLiteral, sb.ToString(), new SourceSpan(start, CurrentLocation()));
    }

    private Token ScanNumber()
    {
        var start = CurrentLocation();
        var startPos = _pos;
        var isFloat = false;

        // Hex, binary, octal prefixes
        if (Current == '0' && _pos + 1 < _source.Length)
        {
            var next = _source[_pos + 1];
            if (next is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
            {
                Advance(); Advance();
                var baseVal = next is 'x' or 'X' ? 16 : next is 'b' or 'B' ? 2 : 8;
                while (_pos < _source.Length && (IsValidDigit(Current, baseVal) || Current == '_'))
                    Advance();
                var lexeme = _source[startPos.._pos];
                // Strip the 0x/0b/0o prefix for Convert.ToInt64
                var digits = lexeme[2..].Replace("_", "");
                return MakeToken(TokenKind.IntegerLiteral, lexeme, new SourceSpan(start, CurrentLocation()))
                    with { LiteralValue = Convert.ToInt64(digits, baseVal) };
            }
        }

        while (_pos < _source.Length && (char.IsDigit(Current) || Current == '_'))
            Advance();

        if (_pos < _source.Length && Current == '.' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
        {
            isFloat = true;
            Advance(); // skip '.'
            while (_pos < _source.Length && (char.IsDigit(Current) || Current == '_'))
                Advance();
        }

        // Scientific notation
        if (_pos < _source.Length && Current is 'e' or 'E')
        {
            isFloat = true;
            Advance();
            if (_pos < _source.Length && Current is '+' or '-')
                Advance();
            while (_pos < _source.Length && char.IsDigit(Current))
                Advance();
        }

        var text = _source[startPos.._pos];
        var cleanText = text.Contains('_') ? text.Replace("_", "") : text;
        var span = new SourceSpan(start, CurrentLocation());

        if (isFloat)
        {
            if (double.TryParse(cleanText, System.Globalization.CultureInfo.InvariantCulture, out var value))
                return MakeToken(TokenKind.FloatLiteral, text, span) with { LiteralValue = value };

            _diagnostics.Error("LEB0003", $"Invalid float literal: {text}", span);
            return MakeToken(TokenKind.FloatLiteral, text, span) with { LiteralValue = 0.0 };
        }
        else
        {
            if (long.TryParse(cleanText, out var value))
                return MakeToken(TokenKind.IntegerLiteral, text, span) with { LiteralValue = value };

            _diagnostics.Error("LEB0003", $"Invalid integer literal: {text}", span);
            return MakeToken(TokenKind.IntegerLiteral, text, span) with { LiteralValue = 0L };
        }
    }

    private Token ScanIdentifierOrKeyword()
    {
        var start = CurrentLocation();
        var startPos = _pos;

        while (_pos < _source.Length && (char.IsLetterOrDigit(Current) || Current == '_'))
            Advance();

        var text = _source[startPos.._pos];
        var span = new SourceSpan(start, CurrentLocation());

        if (text == "_")
            return MakeToken(TokenKind.Underscore, text, span);

        if (Keywords.TryGetValue(text, out var kind))
        {
            var token = MakeToken(kind, text, span);
            if (kind == TokenKind.BoolLiteral)
                return token with { LiteralValue = text == "True" };
            return token;
        }

        return MakeToken(TokenKind.Identifier, text, span);
    }

    private Token ScanAtPrefix()
    {
        var start = CurrentLocation();
        Advance(); // skip '@'

        // @identifier for field access
        if (_pos < _source.Length && (char.IsLetter(Current) || Current == '_'))
        {
            var startPos = _pos;
            while (_pos < _source.Length && (char.IsLetterOrDigit(Current) || Current == '_'))
                Advance();
            var name = _source[startPos.._pos];
            return MakeToken(TokenKind.AtIdentifier, "@" + name, new SourceSpan(start, CurrentLocation()));
        }

        // Standalone @ for decorators
        return MakeToken(TokenKind.At, "@", SourceSpan.From(start));
    }

    private Token ScanOperatorOrDelimiter()
    {
        var start = CurrentLocation();
        var ch = Current;
        Advance();

        switch (ch)
        {
            case '(':
                _parenDepth++;
                return MakeToken(TokenKind.LeftParen, "(", SourceSpan.From(start));
            case ')':
                _parenDepth = Math.Max(0, _parenDepth - 1);
                return MakeToken(TokenKind.RightParen, ")", SourceSpan.From(start));
            case '[':
                _parenDepth++;
                return MakeToken(TokenKind.LeftBracket, "[", SourceSpan.From(start));
            case ']':
                _parenDepth = Math.Max(0, _parenDepth - 1);
                return MakeToken(TokenKind.RightBracket, "]", SourceSpan.From(start));
            case '{':
                _parenDepth++;
                return MakeToken(TokenKind.LeftBrace, "{", SourceSpan.From(start));
            case '}':
                _parenDepth = Math.Max(0, _parenDepth - 1);
                return MakeToken(TokenKind.RightBrace, "}", SourceSpan.From(start));
            case ',':
                return MakeToken(TokenKind.Comma, ",", SourceSpan.From(start));
            case ':':
                return MakeToken(TokenKind.Colon, ":", SourceSpan.From(start));
            case ';':
                return MakeToken(TokenKind.Semicolon, ";", SourceSpan.From(start));
            case '~':
                return MakeToken(TokenKind.Tilde, "~", SourceSpan.From(start));
            case '?':
                return MakeToken(TokenKind.Question, "?", SourceSpan.From(start));

            case '.':
                if (_pos + 1 < _source.Length && Current == '.' && _source[_pos + 1] == '.')
                {
                    Advance(); Advance();
                    return MakeToken(TokenKind.Ellipsis, "...", new SourceSpan(start, CurrentLocation()));
                }
                return MakeToken(TokenKind.Dot, ".", SourceSpan.From(start));

            case '+':
                if (TryConsume('=')) return MakeToken(TokenKind.PlusAssign, "+=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.Plus, "+", SourceSpan.From(start));

            case '-':
                if (TryConsume('>')) return MakeToken(TokenKind.Arrow, "->", new SourceSpan(start, CurrentLocation()));
                if (TryConsume('=')) return MakeToken(TokenKind.MinusAssign, "-=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.Minus, "-", SourceSpan.From(start));

            case '*':
                if (TryConsume('*'))
                {
                    if (TryConsume('=')) return MakeToken(TokenKind.DoubleStarAssign, "**=", new SourceSpan(start, CurrentLocation()));
                    return MakeToken(TokenKind.DoubleStar, "**", new SourceSpan(start, CurrentLocation()));
                }
                if (TryConsume('=')) return MakeToken(TokenKind.StarAssign, "*=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.Star, "*", SourceSpan.From(start));

            case '/':
                if (TryConsume('/'))
                {
                    if (TryConsume('=')) return MakeToken(TokenKind.DoubleSlashAssign, "//=", new SourceSpan(start, CurrentLocation()));
                    return MakeToken(TokenKind.DoubleSlash, "//", new SourceSpan(start, CurrentLocation()));
                }
                if (TryConsume('=')) return MakeToken(TokenKind.SlashAssign, "/=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.Slash, "/", SourceSpan.From(start));

            case '%':
                if (TryConsume('=')) return MakeToken(TokenKind.PercentAssign, "%=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.Percent, "%", SourceSpan.From(start));

            case '&':
                if (TryConsume('=')) return MakeToken(TokenKind.AmpersandAssign, "&=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.Ampersand, "&", SourceSpan.From(start));

            case '|':
                if (TryConsume('=')) return MakeToken(TokenKind.PipeAssign, "|=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.Pipe, "|", SourceSpan.From(start));

            case '^':
                if (TryConsume('=')) return MakeToken(TokenKind.CaretAssign, "^=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.Caret, "^", SourceSpan.From(start));

            case '=':
                if (TryConsume('=')) return MakeToken(TokenKind.Equal, "==", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.Assign, "=", SourceSpan.From(start));

            case '!':
                if (TryConsume('=')) return MakeToken(TokenKind.NotEqual, "!=", new SourceSpan(start, CurrentLocation()));
                _diagnostics.Error("LEB0004", "Unexpected character '!'", SourceSpan.From(start));
                return NextToken();

            case '<':
                if (TryConsume('<'))
                {
                    if (TryConsume('=')) return MakeToken(TokenKind.ShiftLeftAssign, "<<=", new SourceSpan(start, CurrentLocation()));
                    return MakeToken(TokenKind.ShiftLeft, "<<", new SourceSpan(start, CurrentLocation()));
                }
                if (TryConsume('=')) return MakeToken(TokenKind.LessEqual, "<=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.LessThan, "<", SourceSpan.From(start));

            case '>':
                if (TryConsume('>'))
                {
                    if (TryConsume('=')) return MakeToken(TokenKind.ShiftRightAssign, ">>=", new SourceSpan(start, CurrentLocation()));
                    return MakeToken(TokenKind.ShiftRight, ">>", new SourceSpan(start, CurrentLocation()));
                }
                if (TryConsume('=')) return MakeToken(TokenKind.GreaterEqual, ">=", new SourceSpan(start, CurrentLocation()));
                return MakeToken(TokenKind.GreaterThan, ">", SourceSpan.From(start));

            default:
                _diagnostics.Error("LEB0004", $"Unexpected character '{ch}'", SourceSpan.From(start));
                return NextToken();
        }
    }

    // ─── Helpers ───

    private char Current => _source[_pos];

    private char Peek(int offset)
    {
        var idx = _pos + offset;
        return idx < _source.Length ? _source[idx] : '\0';
    }

    private void Advance()
    {
        if (_pos < _source.Length)
        {
            if (_source[_pos] == '\n')
            {
                _line++;
                _col = 1;
            }
            else
            {
                _col++;
            }
            _pos++;
        }
    }

    private bool TryConsume(char expected)
    {
        if (_pos < _source.Length && Current == expected)
        {
            Advance();
            return true;
        }
        return false;
    }

    private void SkipSpaces()
    {
        while (_pos < _source.Length && Current is ' ' or '\t')
            Advance();
    }

    private void SkipComment()
    {
        while (_pos < _source.Length && Current != '\n')
            Advance();
    }

    private SourceLocation CurrentLocation() => new(_filePath, _line, _col);

    private static bool IsValidDigit(char c, int baseVal) => baseVal switch
    {
        2 => c is '0' or '1',
        8 => c >= '0' && c <= '7',
        16 => char.IsAsciiHexDigit(c),
        _ => char.IsDigit(c),
    };

    private static Token MakeToken(TokenKind kind, string lexeme, SourceSpan span)
        => new(kind, lexeme, span);
}
