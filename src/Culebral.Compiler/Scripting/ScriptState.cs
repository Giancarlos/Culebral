namespace Culebral.Scripting;

/// <summary>
/// A variable captured from script execution.
/// </summary>
public sealed record ScriptVariable(string Name, Type Type, object? Value);

/// <summary>
/// Captures the state of a script execution for REPL-style chaining.
/// Each state holds the accumulated source code so that ContinueWith
/// can append new code and re-compile with all prior definitions visible.
/// </summary>
public sealed class ScriptState<T>
{
    /// <summary>The script's return value (from the last expression/print).</summary>
    public T ReturnValue { get; }

    /// <summary>Captured stdout from execution.</summary>
    public string Output { get; }

    /// <summary>Exception thrown during execution, if any.</summary>
    public Exception? Exception { get; }

    /// <summary>True if execution completed without error.</summary>
    public bool Success => Exception is null;

    /// <summary>Accumulated source code from all prior continuations.</summary>
    internal string AccumulatedSource { get; }

    internal ScriptState(T returnValue, string output, Exception? exception, string accumulatedSource)
    {
        ReturnValue = returnValue;
        Output = output;
        Exception = exception;
        AccumulatedSource = accumulatedSource;
    }

    /// <summary>
    /// Continue execution with additional code. The new code can reference
    /// variables and functions defined in prior continuations.
    /// </summary>
    public ScriptState<TNew> ContinueWith<TNew>(string code)
    {
        // Append new code after prior accumulated source
        // Strip the def main() wrapper from prior source, combine, re-wrap
        var combined = AccumulatedSource + "\n" + code;
        var wrapped = CulebralScript.WrapAsScript(combined);

        var diagnostics = new Culebral.Compiler.Diagnostics.DiagnosticBag();
        var (assemblyBytes, pdbBytes) = CulebralScript.CompileToBytes(wrapped, diagnostics);
        if (diagnostics.HasErrors)
            throw new CulebralScriptException("Continuation failed:\n" + diagnostics.FormatAll());

        var alc = new ScriptLoadContext();
        using var peStream = new MemoryStream(assemblyBytes);
        using var pdbStream = pdbBytes != null ? new MemoryStream(pdbBytes) : null;
        var assembly = alc.LoadFromStream(peStream, pdbStream);
        var entryPoint = assembly.EntryPoint
            ?? throw new CulebralScriptException("No entry point found.");

        string output;
        Exception? error = null;
        lock (CulebralScript.ConsoleLock)
        {
            var oldOut = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                entryPoint.Invoke(null, entryPoint.GetParameters().Length > 0 ? new object?[] { null } : null);
            }
            catch (System.Reflection.TargetInvocationException ex) { error = ex.InnerException ?? ex; }
            catch (Exception ex) { error = ex; }
            finally { Console.SetOut(oldOut); }
            output = sw.ToString();
        }

        alc.Unload();

        TNew result = default!;
        if (error is null)
        {
            var trimmed = output.TrimEnd();
            if (typeof(TNew) == typeof(string)) result = (TNew)(object)trimmed;
            else if (typeof(TNew) == typeof(object)) result = (TNew)(object)trimmed;
            else if (typeof(TNew) == typeof(int) && int.TryParse(trimmed, out var i)) result = (TNew)(object)i;
            else if (typeof(TNew) == typeof(double) && double.TryParse(trimmed, out var d)) result = (TNew)(object)d;
            else if (typeof(TNew) == typeof(bool) && bool.TryParse(trimmed, out var b)) result = (TNew)(object)b;
        }

        return new ScriptState<TNew>(result, output, error, combined);
    }
}
