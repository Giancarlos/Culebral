namespace Skelvon.Compiler.Diagnostics;

/// <summary>
/// Immutable representation of a position in source code.
/// Used for error reporting and debugging throughout the compiler pipeline.
/// </summary>
public readonly record struct SourceLocation(string FilePath, int Line, int Column)
{
    public static readonly SourceLocation None = new("<unknown>", 0, 0);

    public override string ToString() => $"{FilePath}:{Line}:{Column}";
}

/// <summary>
/// A span of source code from start to end positions.
/// </summary>
public readonly record struct SourceSpan(SourceLocation Start, SourceLocation End)
{
    public static readonly SourceSpan None = new(SourceLocation.None, SourceLocation.None);

    public static SourceSpan From(SourceLocation loc) => new(loc, loc);

    public override string ToString() => Start == End
        ? Start.ToString()
        : $"{Start}-{End.Line}:{End.Column}";
}
