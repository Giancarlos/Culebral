using Skelvon.Compiler.Diagnostics;

namespace Skelvon.Compiler.Lexer;

/// <summary>
/// A single token produced by the lexer.
/// Immutable value type for cache-friendly iteration.
/// </summary>
public readonly record struct Token(
    TokenKind Kind,
    string Lexeme,
    SourceSpan Span)
{
    /// <summary>
    /// For numeric/bool literals, the parsed value. Null for non-literal tokens.
    /// </summary>
    public object? LiteralValue { get; init; }

    public override string ToString() => $"{Kind}({Lexeme}) at {Span.Start}";
}
