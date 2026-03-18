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

    // ─── Phase 1 Completion Tests ───

    [Fact]
    public void MutualRecursion_Works()
    {
        var output = CompileAndRun("""
            def is_even(n: int) -> bool:
                if n == 0:
                    return True
                return is_odd(n - 1)

            def is_odd(n: int) -> bool:
                if n == 0:
                    return False
                return is_even(n - 1)

            def main():
                print(is_even(4))
                print(is_odd(3))
            """);
        Assert.Equal("True\nTrue", output);
    }

    [Fact]
    public void FloatArithmetic_Works()
    {
        var output = CompileAndRun("""
            def main():
                x = 3.14
                y = 2.0
                print(x + y)
            """);
        Assert.StartsWith("5.14", output);
    }

    [Fact]
    public void NestedForLoops_Work()
    {
        var output = CompileAndRun("""
            def main():
                for i in range(3):
                    for j in range(2):
                        print(i * 10 + j)
            """);
        Assert.Equal("0\n1\n10\n11\n20\n21", output);
    }

    [Fact]
    public void DefaultParameters_Work()
    {
        var output = CompileAndRun("""
            def greet(name: str, times: int = 1) -> str:
                result = ""
                i = 0
                while i < times:
                    result = result + name
                    i += 1
                return result

            def main():
                print(greet("Hi"))
                print(greet("Ha", 3))
            """);
        Assert.Equal("Hi\nHaHaHa", output);
    }

    [Fact]
    public void BooleanLogic_Works()
    {
        var output = CompileAndRun("""
            def main():
                print(True and False)
                print(True or False)
                print(not True)
                print(not False)
            """);
        Assert.Equal("False\nTrue\nFalse\nTrue", output);
    }

    [Fact]
    public void ComparisonOperators_Work()
    {
        var output = CompileAndRun("""
            def main():
                print(5 > 3)
                print(5 < 3)
                print(5 == 5)
                print(5 != 3)
                print(5 >= 5)
                print(5 <= 4)
            """);
        Assert.Equal("True\nFalse\nTrue\nTrue\nTrue\nFalse", output);
    }

    [Fact]
    public void BreakContinue_Work()
    {
        var output = CompileAndRun("""
            def main():
                i = 0
                while i < 10:
                    i += 1
                    if i == 3:
                        continue
                    if i == 6:
                        break
                    print(i)
            """);
        Assert.Equal("1\n2\n4\n5", output);
    }

    [Fact]
    public void AnnotatedVariables_Work()
    {
        var output = CompileAndRun("""
            def main():
                x: int = 42
                y: str = "hello"
                print(x)
                print(y)
            """);
        Assert.Equal("42\nhello", output);
    }

    [Fact]
    public void ForLoopArithmetic_Works()
    {
        var output = CompileAndRun("""
            def main():
                total = 0
                for i in range(5):
                    total += i
                print(total)
            """);
        Assert.Equal("10", output);
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

    // ─── Phase 2 Completion: Structs, Records, Properties ───

    [Fact]
    public void StructWithMethods_Works()
    {
        var output = CompileAndRun("""
            struct Point:
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
    public void RecordWithMethods_Works()
    {
        var output = CompileAndRun("""
            record User:
                name: str = ""
                age: int = 0

                def __init__(name: str, age: int):
                    @name = name
                    @age = age

                def greet() -> str:
                    return f"Hi, {@name}"

            def main():
                u = User("Alice", 30)
                print(u.greet())
            """);
        Assert.Equal("Hi, Alice", output);
    }

    [Fact]
    public void PropertyGetter_Works()
    {
        var output = CompileAndRun("""
            class Box:
                _val: int = 0

                def __init__(v: int):
                    _val = v

                prop val -> int:
                    get: return _val

            def main():
                b = Box(42)
                print(b.val)
            """);
        Assert.Equal("42", output);
    }

    [Fact]
    public void PropertyGetterAndSetter_Works()
    {
        var output = CompileAndRun("""
            class Box:
                _val: int = 0

                def __init__(v: int):
                    _val = v

                prop val -> int:
                    get: return _val
                    set: _val = value

            def main():
                b = Box(42)
                print(b.val)
                b.val = 99
                print(b.val)
            """);
        Assert.Equal("42\n99", output);
    }

    [Fact]
    public void ComputedProperty_Works()
    {
        var output = CompileAndRun("""
            class Temperature:
                _celsius: float = 0.0

                def __init__(c: float):
                    _celsius = c

                prop celsius -> float:
                    get: return _celsius
                    set: _celsius = value

                prop fahrenheit -> float:
                    get: return _celsius * 1.8 + 32.0

            def main():
                t = Temperature(100.0)
                print(t.celsius)
                print(t.fahrenheit)
                t.celsius = 0.0
                print(t.fahrenheit)
            """);
        Assert.Equal("100\n212\n32", output);
    }

    [Fact]
    public void StructMultipleInstances_Works()
    {
        var output = CompileAndRun("""
            struct Vec2:
                x: int = 0
                y: int = 0

                def __init__(x: int, y: int):
                    @x = x
                    @y = y

                def magnitude_sq() -> int:
                    return @x * @x + @y * @y

            def main():
                a = Vec2(3, 4)
                b = Vec2(5, 12)
                print(a.magnitude_sq())
                print(b.magnitude_sq())
            """);
        Assert.Equal("25\n169", output);
    }

    // ─── Phase 2 Completion: Generics ───

    [Fact]
    public void GenericClass_WithInt_Works()
    {
        var output = CompileAndRun("""
            class Box[T]:
                value: T

                def __init__(v: T):
                    value = v

                def get_value() -> T:
                    return value

            def main():
                b = Box(42)
                print(b.get_value())
            """);
        Assert.Equal("42", output);
    }

    [Fact]
    public void GenericClass_WithString_Works()
    {
        var output = CompileAndRun("""
            class Box[T]:
                value: T

                def __init__(v: T):
                    value = v

                def get_value() -> T:
                    return value

            def main():
                b = Box("hello")
                print(b.get_value())
            """);
        Assert.Equal("hello", output);
    }

    [Fact]
    public void GenericClass_MultipleTypeParams_Works()
    {
        var output = CompileAndRun("""
            class Pair[A, B]:
                first: A
                second: B

                def __init__(a: A, b: B):
                    first = a
                    second = b

                def get_first() -> A:
                    return first

                def get_second() -> B:
                    return second

            def main():
                p = Pair(42, "hello")
                print(p.get_first())
                print(p.get_second())
            """);
        Assert.Equal("42\nhello", output);
    }

    [Fact]
    public void GenericClass_DifferentInstantiations_Works()
    {
        var output = CompileAndRun("""
            class Box[T]:
                value: T

                def __init__(v: T):
                    value = v

                def get_value() -> T:
                    return value

            def main():
                int_box = Box(42)
                str_box = Box("world")
                print(int_box.get_value())
                print(str_box.get_value())
            """);
        Assert.Equal("42\nworld", output);
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
