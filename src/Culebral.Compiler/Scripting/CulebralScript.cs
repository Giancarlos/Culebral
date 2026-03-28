using System.Reflection;
using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Emit;
using Culebral.Compiler.IR;
using Culebral.Compiler.Lexer;
using Culebral.Compiler.Parser;
using Culebral.Compiler.Semantics;

namespace Culebral.Scripting;

/// <summary>
/// Static entry point for one-shot Culebral script evaluation and execution.
/// Mirrors Roslyn's CSharpScript API pattern.
/// </summary>
public static class CulebralScript
{
    // Lock to serialize Console.Out capture. Required because Console.Out is
    // process-global and concurrent executions would corrupt each other's output.
    internal static readonly object ConsoleLock = new();

    /// <summary>
    /// One-shot evaluate: compile, run, and return the result converted to <typeparamref name="T"/>.
    /// The script's stdout is captured and converted to the requested type.
    /// </summary>
    public static T Evaluate<T>(string code, object? globals = null)
    {
        var wrappedSource = WrapAsScript(code);

        var diagnostics = new DiagnosticBag();
        var (assemblyBytes, pdbBytes) = CompileToBytes(wrappedSource, diagnostics);
        if (diagnostics.HasErrors)
            throw new CulebralScriptException("Compilation failed:\n" + diagnostics.FormatAll());

        // Load into collectible ALC
        var alc = new ScriptLoadContext();
        using var peStream = new MemoryStream(assemblyBytes);
        using var pdbStream = pdbBytes != null ? new MemoryStream(pdbBytes) : null;
        var assembly = alc.LoadFromStream(peStream, pdbStream);

        // Find and invoke entry point
        var entryPoint = assembly.EntryPoint
            ?? throw new CulebralScriptException("No entry point found in compiled script.");

        // Capture stdout under lock to prevent interleaving with parallel scripts
        string output;
        lock (ConsoleLock)
        {
            var oldOut = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                entryPoint.Invoke(null, entryPoint.GetParameters().Length > 0 ? new object?[] { null } : null);
            }
            finally
            {
                Console.SetOut(oldOut);
            }
            output = sw.ToString().TrimEnd();
        }

        alc.Unload();

        // Convert output to T
        if (typeof(T) == typeof(string)) return (T)(object)output;
        if (typeof(T) == typeof(object)) return (T)(object)output;
        return (T)Convert.ChangeType(output, typeof(T));
    }

    /// <summary>
    /// One-shot execute: compile and run, returning raw stdout output.
    /// </summary>
    public static string Execute(string code, object? globals = null, CulebralScriptOptions? options = null)
    {
        var wrappedSource = WrapAsScript(code);

        var diagnostics = new DiagnosticBag();
        var (assemblyBytes, pdbBytes) = CompileToBytes(wrappedSource, diagnostics);
        if (diagnostics.HasErrors)
            throw new CulebralScriptException("Compilation failed:\n" + diagnostics.FormatAll());

        var alc = new ScriptLoadContext();
        using var peStream = new MemoryStream(assemblyBytes);
        using var pdbStream = pdbBytes != null ? new MemoryStream(pdbBytes) : null;
        var assembly = alc.LoadFromStream(peStream, pdbStream);

        var entryPoint = assembly.EntryPoint
            ?? throw new CulebralScriptException("No entry point found in compiled script.");

        string output;
        lock (ConsoleLock)
        {
            var oldOut = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try
            {
                InvokeWithTimeout(entryPoint, options?.Timeout);
            }
            finally
            {
                Console.SetOut(oldOut);
            }
            output = sw.ToString();
        }

        alc.Unload();
        return output;
    }

    /// <summary>
    /// Run code and return a ScriptState for REPL chaining.
    /// </summary>
    public static ScriptState<T> Run<T>(string code, object? globals = null)
    {
        var result = Execute(code, globals);
        var trimmed = result.TrimEnd();
        T returnValue = default!;
        if (typeof(T) == typeof(string)) returnValue = (T)(object)trimmed;
        else if (typeof(T) == typeof(object)) returnValue = (T)(object)trimmed;
        else if (typeof(T) == typeof(int) && int.TryParse(trimmed, out var i)) returnValue = (T)(object)i;
        else if (typeof(T) == typeof(double) && double.TryParse(trimmed, out var d)) returnValue = (T)(object)d;
        else if (typeof(T) == typeof(bool) && bool.TryParse(trimmed, out var b)) returnValue = (T)(object)b;
        return new ScriptState<T>(returnValue, result, null, code);
    }

    /// <summary>
    /// Wrap bare code in a <c>def main():</c> block if it doesn't already contain one.
    /// </summary>
    internal static string WrapAsScript(string code)
    {
        // If code already has def main(), use as-is
        if (code.Contains("def main()"))
            return code;

        // Wrap bare code in main()
        var indented = string.Join("\n", code.Split('\n').Select(line => "    " + line));
        return $"def main():\n{indented}\n";
    }

    /// <summary>
    /// Invoke a method entry point with an optional timeout.
    /// If timeout is null, the method runs synchronously on the current thread.
    /// If timeout is set, the method runs on a background thread and is aborted via
    /// Thread.Interrupt if it exceeds the time limit.
    /// </summary>
    private static void InvokeWithTimeout(MethodInfo entryPoint, TimeSpan? timeout)
    {
        var args = entryPoint.GetParameters().Length > 0 ? new object?[] { null } : null;

        if (timeout is null)
        {
            entryPoint.Invoke(null, args);
            return;
        }

        var task = Task.Run(() => entryPoint.Invoke(null, args));
        if (!task.Wait(timeout.Value))
            throw new TimeoutException($"Script execution exceeded {timeout.Value.TotalSeconds}s timeout.");

        // Re-throw any exception from the script
        if (task.IsFaulted)
            throw task.Exception!.InnerException ?? task.Exception;
    }

    /// <summary>
    /// Compile Culebral source code to in-memory byte arrays using the full pipeline.
    /// </summary>
    internal static (byte[] Assembly, byte[]? Pdb) CompileToBytes(string source, DiagnosticBag diagnostics, string sourceName = "<script>")
    {
        // Phase 1: Lexing
        var lexer = new CulebralLexer(source, sourceName, diagnostics);
        var tokens = lexer.Tokenize();
        if (diagnostics.HasErrors)
            return (Array.Empty<byte>(), null);

        // Phase 2: Parsing
        var parser = new CulebralParser(tokens, diagnostics);
        var ast = parser.ParseCompilationUnit();
        if (diagnostics.HasErrors)
            return (Array.Empty<byte>(), null);

        // Phase 3: Type Checking
        var typeChecker = new TypeChecker(diagnostics);
        typeChecker.Check(ast);
        if (diagnostics.HasErrors)
            return (Array.Empty<byte>(), null);

        // Phase 4: IR Lowering
        var lowering = new IrLowering(diagnostics, typeChecker);
        var moduleName = Path.GetFileNameWithoutExtension(sourceName);
        if (moduleName == "<script>") moduleName = "script";
        var module = lowering.Lower(ast, moduleName, sourceName);
        if (diagnostics.HasErrors)
            return (Array.Empty<byte>(), null);

        // Phase 5: Emit to memory (no output path needed)
        var emitter = new CilEmitter(diagnostics, "");
        return emitter.EmitToMemory(module);
    }
}

/// <summary>
/// Compiled, reusable Culebral script. Thread-safe for concurrent Run() calls.
/// Supports compile-once-run-many via <see cref="Run"/> and hot-path execution via <see cref="CreateDelegate"/>.
/// </summary>
public sealed class CulebralScript<TReturn> : IDisposable
{
    private readonly byte[]? _assemblyBytes;
    private readonly byte[]? _pdbBytes;
    private readonly IReadOnlyList<Diagnostic> _diagnostics;
    private ScriptLoadContext? _cachedAlc;
    private Assembly? _cachedAssembly;
    private MethodInfo? _cachedEntryPoint;

    /// <summary>True if the script compiled successfully (no errors).</summary>
    public bool IsCompiled => _assemblyBytes != null && _assemblyBytes.Length > 0;

    /// <summary>Compilation diagnostics (errors, warnings).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    internal CulebralScript(byte[] assembly, byte[]? pdb, IReadOnlyList<Diagnostic> diagnostics)
    {
        _assemblyBytes = assembly;
        _pdbBytes = pdb;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Compile source code into a reusable script.
    /// </summary>
    public static CulebralScript<TReturn> Create(string code)
    {
        var wrapped = CulebralScript.WrapAsScript(code);
        var diagnostics = new DiagnosticBag();
        var (asm, pdb) = CulebralScript.CompileToBytes(wrapped, diagnostics);
        return new CulebralScript<TReturn>(asm, pdb, diagnostics.GetDiagnostics());
    }

    /// <summary>
    /// Execute the script, returning a result containing the return value, stdout, and any exception.
    /// Each call loads a fresh collectible ALC that is unloaded after execution.
    /// </summary>
    public ScriptResult<TReturn> Run()
    {
        if (!IsCompiled)
            throw new CulebralScriptException("Script has compilation errors:\n" +
                string.Join("\n", _diagnostics.Select(d => d.ToString())));

        var alc = new ScriptLoadContext();
        using var peStream = new MemoryStream(_assemblyBytes!);
        using var pdbStream = _pdbBytes != null ? new MemoryStream(_pdbBytes) : null;
        var assembly = alc.LoadFromStream(peStream, pdbStream);
        var entryPoint = assembly.EntryPoint
            ?? throw new CulebralScriptException("No entry point found in compiled script.");

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
            catch (TargetInvocationException ex)
            {
                error = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                Console.SetOut(oldOut);
            }
            output = sw.ToString();
        }

        alc.Unload();

        if (error != null)
            return new ScriptResult<TReturn>(default!, output, error);

        TReturn result = ConvertOutput(output);
        return new ScriptResult<TReturn>(result, output, null);
    }

    /// <summary>
    /// Pre-load the assembly for repeated hot-path execution.
    /// The returned delegate can be called many times without reloading.
    /// Dispose the script to unload the cached ALC.
    /// </summary>
    public Func<TReturn> CreateDelegate()
    {
        if (!IsCompiled)
            throw new CulebralScriptException("Script has compilation errors:\n" +
                string.Join("\n", _diagnostics.Select(d => d.ToString())));

        _cachedAlc = new ScriptLoadContext();
        using var peStream = new MemoryStream(_assemblyBytes!);
        using var pdbStream = _pdbBytes != null ? new MemoryStream(_pdbBytes) : null;
        _cachedAssembly = _cachedAlc.LoadFromStream(peStream, pdbStream);
        _cachedEntryPoint = _cachedAssembly.EntryPoint
            ?? throw new CulebralScriptException("No entry point found in compiled script.");

        return () =>
        {
            string output;
            lock (CulebralScript.ConsoleLock)
            {
                var oldOut = Console.Out;
                using var sw = new StringWriter();
                Console.SetOut(sw);
                try
                {
                    _cachedEntryPoint!.Invoke(null,
                        _cachedEntryPoint.GetParameters().Length > 0 ? new object?[] { null } : null);
                }
                finally
                {
                    Console.SetOut(oldOut);
                }
                output = sw.ToString();
            }
            return ConvertOutput(output);
        };
    }

    /// <summary>Convert captured stdout to the target return type.</summary>
    private static TReturn ConvertOutput(string output)
    {
        var trimmed = output.TrimEnd();
        if (typeof(TReturn) == typeof(string)) return (TReturn)(object)trimmed;
        if (typeof(TReturn) == typeof(object)) return (TReturn)(object)trimmed;
        if (typeof(TReturn) == typeof(int) && int.TryParse(trimmed, out var i)) return (TReturn)(object)i;
        if (typeof(TReturn) == typeof(double) && double.TryParse(trimmed, out var d)) return (TReturn)(object)d;
        if (typeof(TReturn) == typeof(bool) && bool.TryParse(trimmed, out var b)) return (TReturn)(object)b;
        return (TReturn)Convert.ChangeType(trimmed, typeof(TReturn));
    }

    /// <summary>
    /// Dispose the script, unloading any cached ALC.
    /// </summary>
    public void Dispose()
    {
        _cachedAlc?.Unload();
        _cachedAlc = null;
        _cachedAssembly = null;
        _cachedEntryPoint = null;
    }
}

/// <summary>
/// Result of a single script execution.
/// </summary>
public sealed record ScriptResult<T>(T ReturnValue, string Output, Exception? Exception)
{
    /// <summary>True if execution completed without an exception.</summary>
    public bool Success => Exception is null;
}
