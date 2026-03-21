namespace Culebral.Scripting;

/// <summary>
/// Embeddable Culebral scripting engine. Allows any .NET application to compile and
/// execute Culebral source code in-process.
///
/// MVP: Globals and functions are stored but not yet injected into scripts (requires
/// compiler changes). The API surface is stable so consumers can build against it now.
/// </summary>
public sealed class CulebralEngine : IDisposable
{
    private readonly Dictionary<string, object?> _globals = new();
    private readonly Dictionary<string, Delegate> _functions = new();
    #pragma warning disable CS0414 // Reserved for future use (in-memory assembly caching)
    private string? _tempDir = null;
    #pragma warning restore CS0414

    // ─── Global Variables (Host ↔ Script) ───

    /// <summary>Expose a host object to scripts as a global variable.</summary>
    public void SetGlobal(string name, object? value) => _globals[name] = value;

    /// <summary>Read a global variable (returns null if not set).</summary>
    public object? GetGlobal(string name) => _globals.GetValueOrDefault(name);

    /// <summary>Remove a global variable.</summary>
    public void RemoveGlobal(string name) => _globals.Remove(name);

    // ─── Host Functions ───

    /// <summary>Register a C# function callable from Culebral scripts.</summary>
    public void SetFunction(string name, Delegate function) => _functions[name] = function;

    /// <summary>Remove a registered function.</summary>
    public void RemoveFunction(string name) => _functions.Remove(name);

    // ─── Execution ───

    /// <summary>
    /// Compile and execute Culebral source code, returning captured stdout.
    /// Throws <see cref="CulebralScriptException"/> on compilation or runtime errors.
    /// </summary>
    public string Execute(string source)
    {
        var (success, output, errors) = Culebral.Compiler.Program.ExecuteSource(source);
        if (!success)
        {
            throw new CulebralScriptException(
                string.IsNullOrWhiteSpace(errors)
                    ? "Script execution failed."
                    : errors.TrimEnd());
        }
        return output;
    }

    /// <summary>
    /// Execute a Culebral script file from disk, returning captured stdout.
    /// </summary>
    public string ExecuteFile(string path)
    {
        var source = File.ReadAllText(path);
        return Execute(source);
    }

    /// <summary>
    /// Evaluate a Culebral expression and return the result converted to <typeparamref name="T"/>.
    /// The expression is wrapped in <c>def main(): print(...)</c>, executed, and the output is parsed.
    /// </summary>
    public T Eval<T>(string expression)
    {
        var source = $"def main():\n    print({expression})";
        var output = Execute(source);
        return (T)Convert.ChangeType(output.Trim(), typeof(T));
    }

    // ─── Lifecycle ───

    public void Dispose()
    {
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }
}

/// <summary>
/// Exception thrown when a Culebral script fails to compile or execute.
/// </summary>
public class CulebralScriptException : Exception
{
    public CulebralScriptException(string message) : base(message) { }
    public CulebralScriptException(string message, Exception innerException) : base(message, innerException) { }
}
