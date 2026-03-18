using System.Collections;
using System.Text;

namespace Skelvon.Compiler.Diagnostics;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    SourceSpan Span)
{
    public override string ToString()
    {
        var severity = Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Info => "info",
            _ => "unknown",
        };
        return $"{Span.Start}: {severity} {Code}: {Message}";
    }
}

/// <summary>
/// Thread-safe, append-only collection of compiler diagnostics.
/// Every phase of the compiler appends to this bag; compilation halts if errors are present.
/// </summary>
public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = [];

    public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public int Count => _diagnostics.Count;

    public void Report(DiagnosticSeverity severity, string code, string message, SourceSpan span)
    {
        _diagnostics.Add(new Diagnostic(severity, code, message, span));
    }

    public void Error(string code, string message, SourceSpan span)
        => Report(DiagnosticSeverity.Error, code, message, span);

    public void Warning(string code, string message, SourceSpan span)
        => Report(DiagnosticSeverity.Warning, code, message, span);

    public void Info(string code, string message, SourceSpan span)
        => Report(DiagnosticSeverity.Info, code, message, span);

    public IReadOnlyList<Diagnostic> GetDiagnostics() => _diagnostics.AsReadOnly();

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public string FormatAll()
    {
        var sb = new StringBuilder();
        foreach (var d in _diagnostics.OrderBy(d => d.Span.Start.Line))
        {
            sb.AppendLine(d.ToString());
        }
        return sb.ToString();
    }
}
