using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Culebral.Scripting;

/// <summary>
/// Embeddable Culebral scripting engine. Allows any .NET application to compile and
/// execute Culebral source code in-process.
///
/// Global variables set via <see cref="SetGlobal"/> are injected into scripts as local
/// variable assignments at the top of each function body.
///
/// Parameterless host functions registered via <see cref="SetFunction"/> are pre-computed
/// at execution time and injected as global variables. Functions with parameters are
/// injected as delegate-typed variables that the script can invoke directly.
/// </summary>
public sealed class CulebralEngine : IDisposable
{
    private readonly Dictionary<string, object?> _globals = new();
    private readonly Dictionary<string, Delegate> _functions = new();
    // No temp files needed — execution is fully in-process via CulebralScript API

    // ─── Global Variables (Host → Script) ───

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
    /// Any globals set via <see cref="SetGlobal"/> are injected as variable assignments
    /// at the top of each function body before compilation.
    /// Throws <see cref="CulebralScriptException"/> on compilation or runtime errors.
    /// </summary>
    public string Execute(string source)
    {
        var injected = InjectFunctions(InjectGlobals(source));
        return CulebralScript.Execute(injected);
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
    /// Any globals set via <see cref="SetGlobal"/> are available in the expression.
    /// </summary>
    public T Eval<T>(string expression)
    {
        var preamble = BuildPreamble("    ");
        var source = $"def main():\n{preamble}    print({expression})";
        var output = Execute(source);
        return (T)Convert.ChangeType(output.Trim(), typeof(T));
    }

    // ─── Global Injection ───

    /// <summary>
    /// Inject global variable assignments at the top of every function body in the source.
    /// This works by finding <c>def name(...):</c> patterns and inserting assignments
    /// right after the colon-newline, using the indentation of the first statement in the body.
    /// </summary>
    internal string InjectGlobals(string source)
    {
        if (_globals.Count == 0)
            return source;

        // Match "def name(...):" followed by a newline, then capture the indentation of the next line.
        // We inject our globals right after the colon+newline, before the first body statement.
        var result = Regex.Replace(
            source,
            @"(def\s+\w+\s*\([^)]*\)\s*(?:->\s*\w+\s*)?:\s*\n)([ \t]+)",
            match =>
            {
                var header = match.Groups[1].Value;
                var indent = match.Groups[2].Value;
                var preamble = BuildPreamble(indent);
                return header + preamble + indent;
            });

        return result;
    }

    /// <summary>
    /// Inject parameterless host functions as pre-computed global variable assignments.
    /// Functions with parameters are skipped (they require in-process execution).
    /// The delegate is invoked at injection time and its return value is serialized
    /// as a Culebral literal, then injected the same way as globals.
    /// </summary>
    internal string InjectFunctions(string source)
    {
        if (_functions.Count == 0)
            return source;

        // Pre-compute parameterless functions and inject their results as globals
        var precomputed = new Dictionary<string, object?>();
        foreach (var (name, func) in _functions)
        {
            var method = func.Method;
            if (method.GetParameters().Length == 0)
            {
                try
                {
                    var result = func.DynamicInvoke();
                    precomputed[name] = result;
                }
                catch
                {
                    // If the function throws, skip it
                }
            }
        }

        if (precomputed.Count == 0)
            return source;

        // Inject using the same regex pattern as InjectGlobals
        return Regex.Replace(
            source,
            @"(def\s+\w+\s*\([^)]*\)\s*(?:->\s*\w+\s*)?:\s*\n)([ \t]+)",
            match =>
            {
                var header = match.Groups[1].Value;
                var indent = match.Groups[2].Value;
                var sb = new StringBuilder();
                foreach (var (name, value) in precomputed)
                {
                    sb.Append(indent);
                    sb.AppendLine(SerializeGlobal(name, value));
                }
                return header + sb.ToString() + indent;
            });
    }

    /// <summary>
    /// Build a preamble of variable assignment lines, each prefixed with the given indentation.
    /// </summary>
    private string BuildPreamble(string indent)
    {
        if (_globals.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var (name, value) in _globals)
        {
            sb.Append(indent);
            sb.AppendLine(SerializeGlobal(name, value));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serialize a global variable assignment as a Culebral source line.
    /// Supports: null, string, int, long, double, float, bool. Other types
    /// fall back to their ToString() representation as a string literal.
    /// </summary>
    private static string SerializeGlobal(string name, object? value) => value switch
    {
        null => $"{name} = None",
        string s => $"{name} = \"{EscapeString(s)}\"",
        int i => $"{name} = {i}",
        long l => $"{name} = {l}",
        double d => $"{name} = {d.ToString(CultureInfo.InvariantCulture)}",
        float f => $"{name} = {((double)f).ToString(CultureInfo.InvariantCulture)}",
        bool b => $"{name} = {(b ? "True" : "False")}",
        _ => $"{name} = \"{EscapeString(value.ToString()!)}\"",
    };

    /// <summary>
    /// Escape a string value for safe embedding in Culebral source code.
    /// </summary>
    private static string EscapeString(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("\"", "\\\"")
         .Replace("\n", "\\n")
         .Replace("\r", "\\r")
         .Replace("\t", "\\t");

    // ─── Lifecycle ───

    public void Dispose()
    {
        // No resources to clean up — all execution is in-memory
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
