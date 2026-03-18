using System.Diagnostics;

namespace Skelvon.Compiler.Tests;

/// <summary>
/// End-to-end tests: compile Skelvon source → .NET assembly → run → assert output.
/// These are the most important tests — they verify the entire pipeline.
/// </summary>
public class EmitTests : IDisposable
{
    private readonly string _tempDir;

    public EmitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"skelvon_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string? CompileAndRun(string source)
    {
        var skvPath = Path.Combine(_tempDir, "test.skv");
        var dllPath = Path.Combine(_tempDir, "test.dll");
        File.WriteAllText(skvPath, source);

        var result = Program.Compile(skvPath, dllPath);
        if (!result.Success)
        {
            Assert.Fail("Compilation failed:\n" + result.Diagnostics.FormatAll());
            return null;
        }

        Assert.True(File.Exists(dllPath), "Output assembly was not created");

        // Run the compiled assembly
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = dllPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        return output.TrimEnd();
    }

    [Fact]
    public void HelloWorld_CompilesAndRuns()
    {
        var output = CompileAndRun("def main():\n    print(\"Hello from Skelvon!\")\n");
        Assert.Equal("Hello from Skelvon!", output);
    }

    [Fact]
    public void IntegerArithmetic_Works()
    {
        var output = CompileAndRun("""
            def main():
                print(2 + 3)
                print(10 - 4)
                print(3 * 7)
                print(10 % 3)
            """);
        Assert.Equal("5\n6\n21\n1", output);
    }

    [Fact]
    public void Variables_Work()
    {
        var output = CompileAndRun("""
            def main():
                x = 42
                y = 8
                z = x + y
                print(z)
            """);
        Assert.Equal("50", output);
    }

    [Fact]
    public void IfElse_Works()
    {
        var output = CompileAndRun("""
            def classify(n: int) -> str:
                if n > 0:
                    return "positive"
                elif n < 0:
                    return "negative"
                else:
                    return "zero"

            def main():
                print(classify(5))
                print(classify(-3))
                print(classify(0))
            """);
        Assert.Equal("positive\nnegative\nzero", output);
    }

    [Fact]
    public void WhileLoop_Works()
    {
        var output = CompileAndRun("""
            def main():
                i = 0
                while i < 5:
                    print(i)
                    i += 1
            """);
        Assert.Equal("0\n1\n2\n3\n4", output);
    }

    [Fact]
    public void RecursiveFunction_Works()
    {
        var output = CompileAndRun("""
            def factorial(n: int) -> int:
                if n <= 1:
                    return 1
                return n * factorial(n - 1)

            def main():
                print(factorial(5))
            """);
        Assert.Equal("120", output);
    }

    [Fact]
    public void Fibonacci_Works()
    {
        var output = CompileAndRun("""
            def fib(n: int) -> int:
                if n <= 1:
                    return n
                return fib(n - 1) + fib(n - 2)

            def main():
                print(fib(10))
            """);
        Assert.Equal("55", output);
    }

    [Fact]
    public void MultipleStringPrints_Work()
    {
        var output = CompileAndRun("""
            def main():
                print("Hello")
                print("World")
            """);
        Assert.Equal("Hello\nWorld", output);
    }

    [Fact]
    public void FunctionWithMultipleParams_Works()
    {
        var output = CompileAndRun("""
            def add(a: int, b: int) -> int:
                return a + b

            def main():
                print(add(3, 4))
            """);
        Assert.Equal("7", output);
    }

    [Fact]
    public void AugmentedAssignment_Works()
    {
        var output = CompileAndRun("""
            def main():
                x = 10
                x += 5
                print(x)
                x -= 3
                print(x)
                x *= 2
                print(x)
            """);
        Assert.Equal("15\n12\n24", output);
    }

    [Fact]
    public void NestedIf_Works()
    {
        var output = CompileAndRun("""
            def main():
                x = 15
                if x > 10:
                    if x > 20:
                        print("big")
                    else:
                        print("medium")
                else:
                    print("small")
            """);
        Assert.Equal("medium", output);
    }

    [Fact]
    public void StringReturn_Works()
    {
        var output = CompileAndRun("""
            def greet(name: str) -> str:
                return name

            def main():
                print(greet("Alice"))
            """);
        Assert.Equal("Alice", output);
    }

    [Fact]
    public void ForRange_Works()
    {
        var output = CompileAndRun("""
            def main():
                for i in range(5):
                    print(i)
            """);
        Assert.Equal("0\n1\n2\n3\n4", output);
    }

    [Fact]
    public void FString_Works()
    {
        var output = CompileAndRun("""
            def main():
                name = "World"
                print(f"Hello, {name}!")
            """);
        Assert.Equal("Hello, World!", output);
    }

    [Fact]
    public void FString_WithInt_Works()
    {
        var output = CompileAndRun("""
            def main():
                x = 42
                print(f"The answer is {x}")
            """);
        Assert.Equal("The answer is 42", output);
    }

    [Fact]
    public void StringConcat_Works()
    {
        var output = CompileAndRun("""
            def main():
                a = "hello"
                b = " world"
                print(a + b)
            """);
        Assert.Equal("hello world", output);
    }

    [Fact]
    public void NestedFunctionCalls_Work()
    {
        var output = CompileAndRun("""
            def square(x: int) -> int:
                return x * x

            def double_it(x: int) -> int:
                return x + x

            def main():
                print(square(double_it(3)))
            """);
        Assert.Equal("36", output);
    }

    // ─── Phase 2: Class Tests ───

    [Fact]
    public void ClassWithConstructor_Works()
    {
        var output = CompileAndRun("""
            class Counter:
                count: int = 0

                def __init__(start: int):
                    count = start

                def get_count() -> int:
                    return count

            def main():
                c = Counter(42)
                print(c.get_count())
            """);
        Assert.Equal("42", output);
    }

    [Fact]
    public void ClassFieldMutation_Works()
    {
        var output = CompileAndRun("""
            class Counter:
                count: int = 0

                def __init__(start: int):
                    count = start

                def increment() -> int:
                    count += 1
                    return count

            def main():
                c = Counter(0)
                print(c.increment())
                print(c.increment())
                print(c.increment())
            """);
        Assert.Equal("1\n2\n3", output);
    }

    [Fact]
    public void ClassAtFieldSyntax_Works()
    {
        var output = CompileAndRun("""
            class Box:
                value: int = 0

                def __init__(value: int):
                    @value = value

                def get_value() -> int:
                    return @value

            def main():
                b = Box(99)
                print(b.get_value())
            """);
        Assert.Equal("99", output);
    }

    [Fact]
    public void MultipleInstances_Independent()
    {
        var output = CompileAndRun("""
            class Counter:
                count: int = 0

                def __init__(start: int):
                    count = start

                def increment() -> int:
                    count += 1
                    return count

            def main():
                a = Counter(0)
                b = Counter(100)
                print(a.increment())
                print(b.increment())
                print(a.increment())
            """);
        Assert.Equal("1\n101\n2", output);
    }

    [Fact]
    public void ClassWithStringField_Works()
    {
        var output = CompileAndRun("""
            class Greeter:
                name: str = ""

                def __init__(name: str):
                    @name = name

                def greet() -> str:
                    return f"Hello, {@name}!"

            def main():
                g = Greeter("World")
                print(g.greet())
            """);
        Assert.Equal("Hello, World!", output);
    }

    [Fact]
    public void ClassMethodWithParams_Works()
    {
        var output = CompileAndRun("""
            class Adder:
                base_val: int = 0

                def __init__(base_val: int):
                    @base_val = base_val

                def add(x: int) -> int:
                    return @base_val + x

            def main():
                a = Adder(10)
                print(a.add(5))
                print(a.add(20))
            """);
        Assert.Equal("15\n30", output);
    }

    [Fact]
    public void ClassDefaultFieldValues_Work()
    {
        var output = CompileAndRun("""
            class Config:
                retries: int = 3
                timeout: int = 30

                def get_retries() -> int:
                    return retries

                def get_timeout() -> int:
                    return timeout

            def main():
                c = Config()
                print(c.get_retries())
                print(c.get_timeout())
            """);
        Assert.Equal("3\n30", output);
    }

    [Fact]
    public void InterfaceImplementation_Works()
    {
        var output = CompileAndRun("""
            interface Describable:
                def describe() -> str

            class Dog(Describable):
                name: str = ""

                def __init__(name: str):
                    @name = name

                def describe() -> str:
                    return f"Dog: {@name}"

            def main():
                d = Dog("Rex")
                print(d.describe())
            """);
        Assert.Equal("Dog: Rex", output);
    }

    [Fact]
    public void ClassWithMultipleFields_Works()
    {
        var output = CompileAndRun("""
            class Point:
                x: int = 0
                y: int = 0

                def __init__(x: int, y: int):
                    @x = x
                    @y = y

                def sum() -> int:
                    return @x + @y

            def main():
                p = Point(3, 7)
                print(p.sum())
            """);
        Assert.Equal("10", output);
    }

    [Fact]
    public void CompilationDiagnostics_ReportErrors()
    {
        var skvPath = Path.Combine(_tempDir, "bad.skv");
        var dllPath = Path.Combine(_tempDir, "bad.dll");
        // Source with unclosed string
        File.WriteAllText(skvPath, "def main():\n    print(\"unclosed\n");

        var result = Program.Compile(skvPath, dllPath);
        Assert.True(result.Diagnostics.HasErrors);
    }
}
