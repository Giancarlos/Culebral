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
