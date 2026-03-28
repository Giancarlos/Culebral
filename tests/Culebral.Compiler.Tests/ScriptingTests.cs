using Culebral.Compiler.Diagnostics;
using Culebral.Scripting;

namespace Culebral.Compiler.Tests;

/// <summary>
/// Tests for the Culebral embedding/scripting API (CulebralEngine).
/// </summary>
public class ScriptingTests : IDisposable
{
    private readonly CulebralEngine _engine = new();

    public void Dispose() => _engine.Dispose();

    // ─── CompileFromSource Tests ───

    [Fact]
    public void CompileFromSource_ValidSource_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"culebral_scripting_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath = Path.Combine(tempDir, "test.dll");
            var result = Program.CompileFromSource("def main():\n    print(\"hello\")", dllPath);
            Assert.True(result.Success, "CompileFromSource should succeed for valid source");
            Assert.True(File.Exists(dllPath), "Output assembly should be created");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void CompileFromSource_InvalidSource_FailsWithDiagnostics()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"culebral_scripting_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath = Path.Combine(tempDir, "test.dll");
            var result = Program.CompileFromSource("def main(:\n    broken", dllPath);
            Assert.False(result.Success, "CompileFromSource should fail for invalid source");
            Assert.True(result.Diagnostics.HasErrors);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // ─── ExecuteSource Tests ───

    [Fact]
    public void ExecuteSource_HelloWorld_ReturnsOutput()
    {
        var (success, output, errors) = Program.ExecuteSource("def main():\n    print(\"hello\")");
        Assert.True(success, $"ExecuteSource should succeed. Errors: {errors}");
        Assert.Equal("hello", output.TrimEnd());
    }

    [Fact]
    public void ExecuteSource_Arithmetic_ReturnsResult()
    {
        var (success, output, errors) = Program.ExecuteSource("def main():\n    print(2 + 3)");
        Assert.True(success, $"ExecuteSource should succeed. Errors: {errors}");
        Assert.Equal("5", output.TrimEnd());
    }

    [Fact]
    public void ExecuteSource_InvalidSource_ReturnsFalse()
    {
        var (success, _, _) = Program.ExecuteSource("this is not valid culebral");
        Assert.False(success);
    }

    // ─── CulebralEngine.Execute Tests ───

    [Fact]
    public void Execute_HelloWorld()
    {
        var output = _engine.Execute("def main():\n    print(\"hello\")");
        Assert.Equal("hello", output.TrimEnd());
    }

    [Fact]
    public void Execute_Arithmetic()
    {
        var output = _engine.Execute("def main():\n    print(2 + 3)");
        Assert.Equal("5", output.TrimEnd());
    }

    [Fact]
    public void Execute_InvalidSource_Throws()
    {
        Assert.Throws<CulebralScriptException>(() =>
            _engine.Execute("this is not valid culebral"));
    }

    // ─── CulebralEngine.Eval Tests ───

    [Fact]
    public void Eval_IntExpression()
    {
        var result = _engine.Eval<int>("2 + 3");
        Assert.Equal(5, result);
    }

    [Fact]
    public void Eval_StringExpression()
    {
        var result = _engine.Eval<string>("\"hello\"");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Eval_BoolExpression()
    {
        var result = _engine.Eval<bool>("True");
        Assert.True(result);
    }

    // ─── CulebralEngine.SetGlobal / GetGlobal Tests ───

    [Fact]
    public void SetGlobal_GetGlobal_RoundTrips()
    {
        _engine.SetGlobal("x", 42);
        Assert.Equal(42, _engine.GetGlobal("x"));
    }

    [Fact]
    public void GetGlobal_Unset_ReturnsNull()
    {
        Assert.Null(_engine.GetGlobal("nonexistent"));
    }

    [Fact]
    public void RemoveGlobal_RemovesValue()
    {
        _engine.SetGlobal("x", 42);
        _engine.RemoveGlobal("x");
        Assert.Null(_engine.GetGlobal("x"));
    }

    // ─── CulebralEngine.SetFunction / RemoveFunction Tests ───

    [Fact]
    public void SetFunction_RemoveFunction_Works()
    {
        Func<int, int> doubler = x => x * 2;
        _engine.SetFunction("double", doubler);
        _engine.RemoveFunction("double");
        // No exception means it worked — function registration is stored for future injection
    }

    // ─── Global Variable Injection Tests ───

    [Fact]
    public void Execute_WithGlobals_InjectsVariables()
    {
        _engine.SetGlobal("name", "Alice");
        _engine.SetGlobal("age", 30);
        var output = _engine.Execute("""
            def main():
                print(f"Hello, {name}! Age: {age}")
            """);
        Assert.Equal("Hello, Alice! Age: 30", output.Trim());
    }

    [Fact]
    public void Eval_WithGlobals_UsesInjectedValues()
    {
        _engine.SetGlobal("x", 10);
        _engine.SetGlobal("y", 20);
        var result = _engine.Eval<int>("x + y");
        Assert.Equal(30, result);
    }

    [Fact]
    public void Execute_WithStringGlobal_EscapesCorrectly()
    {
        _engine.SetGlobal("msg", "hello \"world\"");
        var output = _engine.Execute("""
            def main():
                print(msg)
            """);
        Assert.Equal("hello \"world\"", output.Trim());
    }

    [Fact]
    public void Execute_WithNoneGlobal()
    {
        _engine.SetGlobal("value", null);
        var output = _engine.Execute("""
            def main():
                if value is None:
                    print("is none")
            """);
        Assert.Equal("is none", output.Trim());
    }

    [Fact]
    public void Execute_WithBoolGlobal()
    {
        _engine.SetGlobal("flag", true);
        var output = _engine.Execute("""
            def main():
                if flag:
                    print("truthy")
            """);
        Assert.Equal("truthy", output.Trim());
    }

    [Fact]
    public void Execute_WithIntGlobal()
    {
        _engine.SetGlobal("count", 42);
        var output = _engine.Execute("""
            def main():
                print(count)
            """);
        Assert.Equal("42", output.Trim());
    }

    [Fact]
    public void Execute_WithFloatGlobal()
    {
        _engine.SetGlobal("pi", 3.14);
        var output = _engine.Execute("""
            def main():
                print(pi)
            """);
        Assert.Equal("3.14", output.Trim());
    }

    [Fact]
    public void Execute_NoGlobals_PassthroughUnchanged()
    {
        // With no globals set, Execute should work exactly as before
        var output = _engine.Execute("def main():\n    print(\"plain\")");
        Assert.Equal("plain", output.Trim());
    }
}

// ─── CulebralScript Static API Tests (Phases 1–3) ───

/// <summary>
/// Tests for the new in-process scripting API: CulebralScript.Evaluate, Execute, and CulebralScript&lt;T&gt;.
/// </summary>
public class CulebralScriptApiTests
{
    [Fact]
    public void Evaluate_IntExpression()
    {
        var result = CulebralScript.Evaluate<int>("print(2 + 2)");
        Assert.Equal(4, result);
    }

    [Fact]
    public void Evaluate_StringExpression()
    {
        var result = CulebralScript.Evaluate<string>("print(\"hello\")");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Execute_ReturnsOutput()
    {
        var output = CulebralScript.Execute("print(\"test\")");
        Assert.Contains("test", output);
    }

    [Fact]
    public void Execute_WithExistingMain_PassesThrough()
    {
        var output = CulebralScript.Execute("def main():\n    print(\"via main\")");
        Assert.Contains("via main", output);
    }

    [Fact]
    public void CompilationError_ThrowsWithDiagnostics()
    {
        var ex = Assert.Throws<CulebralScriptException>(() =>
            CulebralScript.Evaluate<int>("definitely not valid culebral!!!"));
        Assert.Contains("Compilation failed", ex.Message);
    }

    // ─── CulebralScript<T> Create/Run/CreateDelegate Tests (Phases 4–6) ───

    [Fact]
    public void Create_ValidCode_IsCompiled()
    {
        var script = CulebralScript<string>.Create("print(\"cached\")");
        Assert.True(script.IsCompiled);
        script.Dispose();
    }

    [Fact]
    public void Create_InvalidCode_HasDiagnostics()
    {
        var script = CulebralScript<string>.Create("this is not valid!!!");
        Assert.False(script.IsCompiled);
        Assert.NotEmpty(script.Diagnostics);
        Assert.Contains(script.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        script.Dispose();
    }

    [Fact]
    public void Create_AndRun_ReturnsResult()
    {
        var script = CulebralScript<string>.Create("print(\"cached\")");
        Assert.True(script.IsCompiled);
        var r1 = script.Run();
        var r2 = script.Run();
        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.Equal("cached", r1.ReturnValue);
        Assert.Equal("cached", r2.ReturnValue);
        script.Dispose();
    }

    [Fact]
    public void CreateDelegate_HotPath()
    {
        using var script = CulebralScript<string>.Create("print(\"fast\")");
        var fn = script.CreateDelegate();
        var result = fn();
        Assert.Equal("fast", result);
    }

    [Fact]
    public void CreateDelegate_MultipleInvocations()
    {
        using var script = CulebralScript<string>.Create("print(\"repeat\")");
        var fn = script.CreateDelegate();
        for (int i = 0; i < 5; i++)
        {
            var result = fn();
            Assert.Equal("repeat", result);
        }
    }

    [Fact]
    public void Run_RuntimeError_CapturesException()
    {
        // Division by zero should cause a runtime error
        var script = CulebralScript<string>.Create("print(1 // 0)");
        if (script.IsCompiled)
        {
            var result = script.Run();
            Assert.False(result.Success);
            Assert.NotNull(result.Exception);
        }
        script.Dispose();
    }

    [Fact]
    public void ScriptResult_Success_Property()
    {
        using var script = CulebralScript<string>.Create("print(\"ok\")");
        var result = script.Run();
        Assert.True(result.Success);
        Assert.Null(result.Exception);
        Assert.Equal("ok", result.ReturnValue);
    }

    // ─── REPL State Chaining Tests ───

    [Fact]
    public void Run_ReturnsScriptState()
    {
        var state = CulebralScript.Run<string>("print(\"hello\")");
        Assert.True(state.Success);
        Assert.Equal("hello", state.ReturnValue);
    }

    [Fact]
    public void ContinueWith_AppendCode()
    {
        // First: define x
        var state1 = CulebralScript.Run<string>("x = 10\nprint(x)");
        Assert.Equal("10", state1.ReturnValue);

        // Continue: use x in new expression. Output includes BOTH prints (re-execution of all code)
        var state2 = state1.ContinueWith<string>("y = x + 20\nprint(y)");
        Assert.Contains("30", state2.Output);
    }

    [Fact]
    public void ContinueWith_PreservesVariables()
    {
        // Variables from prior states are visible in continuations
        var s1 = CulebralScript.Run<string>("x = 42");
        var s2 = s1.ContinueWith<string>("print(x)");
        Assert.Contains("42", s2.Output);
    }

    // ─── Options Tests ───

    [Fact]
    public void Options_Default_NoTimeout()
    {
        var opts = CulebralScriptOptions.Default;
        Assert.Null(opts.Timeout);
        Assert.True(opts.AllowFileSystemAccess);
        Assert.True(opts.AllowNetworkAccess);
    }

    [Fact]
    public void Options_WithTimeout_Immutable()
    {
        var opts1 = CulebralScriptOptions.Default;
        var opts2 = opts1.WithTimeout(TimeSpan.FromSeconds(5));
        Assert.Null(opts1.Timeout); // original unchanged
        Assert.Equal(TimeSpan.FromSeconds(5), opts2.Timeout);
    }

    [Fact]
    public void Options_Chaining()
    {
        var opts = CulebralScriptOptions.Default
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithFileSystemAccess(false)
            .WithNetworkAccess(false);
        Assert.Equal(TimeSpan.FromSeconds(10), opts.Timeout);
        Assert.False(opts.AllowFileSystemAccess);
        Assert.False(opts.AllowNetworkAccess);
    }

    // ─── Lambda Closure Tests ───

    [Fact]
    public void Lambda_CapturesOuterVariable()
    {
        var output = CulebralScript.Execute("""
            x = 42
            f = lambda: x
            print(f())
            """);
        Assert.Contains("42", output);
    }

    [Fact]
    public void Lambda_CapturesMultipleVariables()
    {
        var output = CulebralScript.Execute("""
            a = 10
            b = 20
            f = lambda: a + b
            print(f())
            """);
        Assert.Contains("30", output);
    }

    [Fact]
    public void FunctionReference_AsDelegate()
    {
        var output = CulebralScript.Execute("""
            def greet() -> str:
                return "hello"
            def main():
                f = greet
                print(f())
            """);
        Assert.Contains("hello", output);
    }

    // ─── Timeout Tests ───

    [Fact]
    public void Execute_WithTimeout_CompletesNormally()
    {
        var opts = CulebralScriptOptions.Default.WithTimeout(TimeSpan.FromSeconds(5));
        var output = CulebralScript.Execute("print(\"ok\")", options: opts);
        Assert.Contains("ok", output);
    }

    [Fact]
    public void Execute_WithTimeout_ThrowsOnExceed()
    {
        var opts = CulebralScriptOptions.Default.WithTimeout(TimeSpan.FromMilliseconds(100));
        Assert.Throws<TimeoutException>(() =>
            CulebralScript.Execute("""
                x = 0
                while True:
                    x = x + 1
                """, options: opts));
    }

    // ─── Input Validation & Exception Handling Tests ───

    [Fact]
    public void Evaluate_NullCode_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => CulebralScript.Evaluate<int>(null!));
    }

    [Fact]
    public void Evaluate_EmptyCode_ThrowsArgument()
    {
        Assert.Throws<ArgumentException>(() => CulebralScript.Evaluate<int>(""));
    }

    [Fact]
    public void Evaluate_InvalidTypeConversion_ThrowsScriptException()
    {
        // Script outputs a string, but we ask for Guid — should throw CulebralScriptException, not InvalidCastException
        Assert.Throws<CulebralScriptException>(() => CulebralScript.Evaluate<Guid>("print('hello')"));
    }

    [Fact]
    public void Evaluate_ScriptThrows_ThrowsScriptException()
    {
        Assert.Throws<CulebralScriptException>(() =>
            CulebralScript.Execute("raise Exception('boom')"));
    }
}
