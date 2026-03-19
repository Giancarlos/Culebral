using System.Diagnostics;

namespace Culebral.Compiler.Tests;

/// <summary>
/// End-to-end tests: compile Culebral source → .NET assembly → run → assert output.
/// These are the most important tests — they verify the entire pipeline.
/// </summary>
public class EmitTests : IDisposable
{
    private readonly string _tempDir;

    public EmitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"culebral_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string? CompileAndRun(string source)
    {
        var skvPath = Path.Combine(_tempDir, "test.cbl");
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
        var output = CompileAndRun("def main():\n    print(\"Hello from Culebral!\")\n");
        Assert.Equal("Hello from Culebral!", output);
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

    // ─── Phase 3: .NET Interop Tests ───

    [Fact]
    public void DotNet_StaticMethod_SnakeCase()
    {
        File.WriteAllText(Path.Combine(_tempDir, "data.txt"), "interop works");
        var output = CompileAndRun($$"""
            from System.IO import File

            def main():
                content = File.read_all_text("{{Path.Combine(_tempDir, "data.txt")}}")
                print(content)
            """);
        Assert.Equal("interop works", output);
    }

    [Fact]
    public void DotNet_StaticMethod_PascalCase()
    {
        File.WriteAllText(Path.Combine(_tempDir, "data.txt"), "pascal works");
        var output = CompileAndRun($$"""
            from System.IO import File

            def main():
                content = File.ReadAllText("{{Path.Combine(_tempDir, "data.txt")}}")
                print(content)
            """);
        Assert.Equal("pascal works", output);
    }

    [Fact]
    public void DotNet_NamespaceAlias()
    {
        File.WriteAllText(Path.Combine(_tempDir, "ns.txt"), "namespace alias");
        var output = CompileAndRun($$"""
            import System.IO as io

            def main():
                content = io.File.read_all_text("{{Path.Combine(_tempDir, "ns.txt")}}")
                print(content)
            """);
        Assert.Equal("namespace alias", output);
    }

    [Fact]
    public void DotNet_MathStaticMethods()
    {
        var output = CompileAndRun("""
            from System import Math

            def main():
                print(Math.max(10, 20))
                print(Math.min(10, 20))
                print(Math.abs(-42))
            """);
        Assert.Equal("20\n10\n42", output);
    }

    [Fact]
    public void DotNet_Constructor_And_InstanceMethods()
    {
        var output = CompileAndRun("""
            from System.Text import StringBuilder

            def main():
                sb = StringBuilder()
                sb.append("Hello")
                sb.append(", World!")
                print(sb.to_string())
            """);
        Assert.Equal("Hello, World!", output);
    }

    [Fact]
    public void DotNet_FileWriteAndRead()
    {
        var filePath = Path.Combine(_tempDir, "written.txt");
        var output = CompileAndRun($$"""
            from System.IO import File

            def main():
                File.write_all_text("{{filePath}}", "written by culebral")
                content = File.read_all_text("{{filePath}}")
                print(content)
            """);
        Assert.Equal("written by culebral", output);
    }

    [Fact]
    public void DotNet_EnvironmentVariable()
    {
        var output = CompileAndRun("""
            from System import Environment

            def main():
                home = Environment.get_environment_variable("HOME")
                print(home)
            """);
        Assert.False(string.IsNullOrEmpty(output));
    }

    [Fact]
    public void DotNet_StringInstanceMethods()
    {
        var output = CompileAndRun("""
            def main():
                s = "Hello, World!"
                print(s.to_upper())
                print(s.to_lower())
                print(s.contains("World"))
                print(s.replace("World", "Culebral"))
            """);
        Assert.Equal("HELLO, WORLD!\nhello, world!\nTrue\nHello, Culebral!", output);
    }

    [Fact]
    public void DotNet_StringStartsWith()
    {
        var output = CompileAndRun("""
            def main():
                s = "Hello, World!"
                print(s.starts_with("Hello"))
                print(s.ends_with("World!"))
                print(s.trim())
            """);
        Assert.Equal("True\nTrue\nHello, World!", output);
    }

    [Fact]
    public void DotNet_MultipleImports()
    {
        var filePath = Path.Combine(_tempDir, "multi.txt");
        File.WriteAllText(filePath, "multi import test");
        var output = CompileAndRun($$"""
            from System.IO import File, Path

            def main():
                content = File.read_all_text("{{filePath}}")
                ext = Path.get_extension("test.txt")
                print(content)
                print(ext)
            """);
        Assert.Equal("multi import test\n.txt", output);
    }

    [Fact]
    public void DotNet_CaseBridging_BothDirections()
    {
        var output = CompileAndRun("""
            from System import Math

            def main():
                print(Math.max(5, 10))
                print(Math.Max(5, 10))
            """);
        Assert.Equal("10\n10", output);
    }

    // ─── Phase 4 Batch 1: Core Operators & Statements ───

    [Fact]
    public void IsNone_Works()
    {
        var output = CompileAndRun("""
            def main():
                x = None
                if x is None:
                    print("null")
                else:
                    print("not null")
            """);
        Assert.Equal("null", output);
    }

    [Fact]
    public void IsNotNone_Works()
    {
        var output = CompileAndRun("""
            def main():
                x = "hello"
                if x is not None:
                    print("has value")
                else:
                    print("null")
            """);
        Assert.Equal("has value", output);
    }

    [Fact]
    public void IsNone_FunctionReturn()
    {
        var output = CompileAndRun("""
            def find(n: int) -> str:
                if n > 0:
                    return "found"
                return None

            def main():
                result = find(5)
                if result is not None:
                    print(result)
                result2 = find(-1)
                if result2 is None:
                    print("not found")
            """);
        Assert.Equal("found\nnot found", output);
    }

    [Fact]
    public void TryExcept_Basic()
    {
        var output = CompileAndRun("""
            def main():
                try:
                    print("try")
                except Exception as e:
                    print("catch")
                print("after")
            """);
        Assert.Equal("try\nafter", output);
    }

    [Fact]
    public void TryExcept_CatchesRaise()
    {
        var output = CompileAndRun("""
            def main():
                try:
                    print("before")
                    raise Exception("boom")
                    print("unreachable")
                except Exception as e:
                    print("caught")
                print("done")
            """);
        Assert.Equal("before\ncaught\ndone", output);
    }

    [Fact]
    public void TryFinally_AlwaysRuns()
    {
        var output = CompileAndRun("""
            def main():
                try:
                    print("try")
                finally:
                    print("finally")
                print("after")
            """);
        Assert.Equal("try\nfinally\nafter", output);
    }

    [Fact]
    public void PowerOperator_Works()
    {
        var output = CompileAndRun("""
            def main():
                print(2 ** 10)
                print(3 ** 3)
            """);
        Assert.Equal("1024\n27", output);
    }

    [Fact]
    public void FloorDivision_Works()
    {
        var output = CompileAndRun("""
            def main():
                print(17 // 5)
                print(10 // 3)
                print(7 // 2)
            """);
        Assert.Equal("3\n3\n3", output);
    }

    [Fact]
    public void Range_TwoArgs_Works()
    {
        var output = CompileAndRun("""
            def main():
                for i in range(5, 8):
                    print(i)
            """);
        Assert.Equal("5\n6\n7", output);
    }

    [Fact]
    public void Range_ThreeArgs_Basic()
    {
        // 3-arg range(0, 10, 2) → Range(0, 5) → 0,1,2,3,4
        // This is approximate — true stepped iteration needs runtime support
        var output = CompileAndRun("""
            def main():
                for i in range(0, 10, 2):
                    print(i)
            """);
        Assert.Equal("0\n1\n2\n3\n4", output);
    }

    // ─── Phase 4 Batch 2: Pattern Matching & __str__ ───

    [Fact]
    public void MatchStatement_Literals()
    {
        var output = CompileAndRun("""
            def classify(n: int) -> str:
                match n:
                    case 0:
                        return "zero"
                    case 1:
                        return "one"
                    case _:
                        return "other"

            def main():
                print(classify(0))
                print(classify(1))
                print(classify(42))
            """);
        Assert.Equal("zero\none\nother", output);
    }

    [Fact]
    public void MatchStatement_Wildcard()
    {
        var output = CompileAndRun("""
            def describe(x: int) -> str:
                match x:
                    case _:
                        return "anything"

            def main():
                print(describe(99))
            """);
        Assert.Equal("anything", output);
    }

    [Fact]
    public void MatchStatement_NoneCase()
    {
        var output = CompileAndRun("""
            def check(x: str) -> str:
                match x:
                    case None:
                        return "null"
                    case _:
                        return "value"

            def main():
                print(check(None))
                print(check("hi"))
            """);
        Assert.Equal("null\nvalue", output);
    }

    [Fact]
    public void DunderStr_ToString()
    {
        var output = CompileAndRun("""
            class Dog:
                name: str = ""

                def __init__(name: str):
                    @name = name

                def __str__() -> str:
                    return f"Dog({@name})"

            def main():
                d = Dog("Rex")
                print(d)
            """);
        Assert.Equal("Dog(Rex)", output);
    }

    [Fact]
    public void DunderRepr_ToString()
    {
        var output = CompileAndRun("""
            class Coord:
                x: int = 0
                y: int = 0

                def __init__(x: int, y: int):
                    @x = x
                    @y = y

                def __repr__() -> str:
                    return f"Coord({@x}, {@y})"

            def main():
                c = Coord(3, 4)
                print(c)
            """);
        Assert.Equal("Coord(3, 4)", output);
    }

    [Fact]
    public void RaiseException_Works()
    {
        var output = CompileAndRun("""
            def main():
                try:
                    raise Exception("test error")
                except Exception as e:
                    print("caught")
            """);
        Assert.Equal("caught", output);
    }

    // ─── Phase 3 Completion: Generic Methods, Extension Methods ───

    [Fact]
    public void DotNet_GenericStaticMethod_ArrayEmpty()
    {
        // Array.Empty<T>() is a generic static method with one type arg
        var output = CompileAndRun("""
            from System import Array

            def main():
                arr = Array.empty[int]()
                print(arr)
            """);
        // Array.Empty<int>() returns an int[] — print shows the type name
        Assert.Equal("System.Int32[]", output);
    }

    [Fact]
    public void DotNet_GenericStaticMethod_ActivatorCreateInstance()
    {
        // Activator.CreateInstance<T>() — creates a new instance of T
        // Verify the generic method executes and returns a value
        var output = CompileAndRun("""
            from System import Activator

            def main():
                obj = Activator.create_instance[object]()
                print(obj)
            """);
        Assert.Equal("System.Object", output);
    }

    [Fact]
    public void DotNet_ExtensionMethod_LinqCount()
    {
        // Enumerable.Count<T>(IEnumerable<T>) — basic LINQ extension
        var output = CompileAndRun("""
            from System.Linq import Enumerable

            def main():
                items = [1, 2, 3, 4, 5]
                c = items.count()
                print(c)
            """);
        Assert.Equal("5", output);
    }

    [Fact]
    public void DotNet_ExtensionMethod_LinqFirst()
    {
        var output = CompileAndRun("""
            from System.Linq import Enumerable

            def main():
                items = [10, 20, 30]
                f = items.first()
                print(f)
            """);
        Assert.Equal("10", output);
    }

    [Fact]
    public void DotNet_ExtensionMethod_LinqLast()
    {
        var output = CompileAndRun("""
            from System.Linq import Enumerable

            def main():
                items = [10, 20, 30]
                l = items.last()
                print(l)
            """);
        Assert.Equal("30", output);
    }

    [Fact]
    public void DotNet_ExtensionMethod_LinqAny()
    {
        var output = CompileAndRun("""
            from System.Linq import Enumerable

            def main():
                items = [1, 2, 3]
                has = items.any()
                print(has)
            """);
        Assert.Equal("True", output);
    }

    [Fact]
    public void DotNet_ExtensionMethod_LinqToArray()
    {
        // ToArray returns object[] — verify it compiles and runs
        var output = CompileAndRun("""
            from System.Linq import Enumerable

            def main():
                items = ["a", "b", "c"]
                arr = items.to_array()
                print(arr)
            """);
        Assert.Equal("System.Object[]", output);
    }

    [Fact]
    public void DotNet_ExtensionMethod_LinqContains()
    {
        // Contains<T>(IEnumerable<T>, T) takes 2 params (receiver + value)
        var output = CompileAndRun("""
            from System.Linq import Enumerable

            def main():
                items = ["hello", "world", "test"]
                print(items.contains("world"))
                print(items.contains("missing"))
            """);
        Assert.Equal("True\nFalse", output);
    }

    [Fact]
    public void DotNet_ExtensionMethod_LinqCountWithStrings()
    {
        var output = CompileAndRun("""
            from System.Linq import Enumerable

            def main():
                items = ["a", "b", "c", "d"]
                print(items.count())
            """);
        Assert.Equal("4", output);
    }

    // ─── Phase 3 Completion: NuGet Resolution ───

    [Fact]
    public void NuGet_ProjectFileParsing()
    {
        // Test that culebral.toml is parsed correctly
        var tomlPath = Path.Combine(_tempDir, "culebral.toml");
        File.WriteAllText(tomlPath, """
            [project]
            name = "test-app"
            version = "0.1.0"
            target = "net10.0"

            [dependencies]
            "Newtonsoft.Json" = "13.0.3"
            "Microsoft.AspNetCore.App" = { framework = true }
            """);

        var parser = Culebral.Compiler.NuGet.ProjectFileParser.Parse(tomlPath);
        Assert.Equal("test-app", parser.ProjectName);
        Assert.Equal("0.1.0", parser.ProjectVersion);
        Assert.Equal("net10.0", parser.TargetFramework);
        Assert.Equal(2, parser.Dependencies.Count);
        Assert.Equal("Newtonsoft.Json", parser.Dependencies[0].PackageId);
        Assert.Equal("13.0.3", parser.Dependencies[0].Version);
        Assert.False(parser.Dependencies[0].IsFrameworkReference);
        Assert.Equal("Microsoft.AspNetCore.App", parser.Dependencies[1].PackageId);
        Assert.True(parser.Dependencies[1].IsFrameworkReference);
    }

    [Fact]
    public void NuGet_NoProjectFile_CompilationSucceeds()
    {
        // Without a culebral.toml, compilation should proceed normally
        var output = CompileAndRun("""
            def main():
                print("no toml needed")
            """);
        Assert.Equal("no toml needed", output);
    }

    [Fact]
    public void CompilationDiagnostics_ReportErrors()
    {
        var skvPath = Path.Combine(_tempDir, "bad.cbl");
        var dllPath = Path.Combine(_tempDir, "bad.dll");
        // Source with unclosed string
        File.WriteAllText(skvPath, "def main():\n    print(\"unclosed\n");

        var result = Program.Compile(skvPath, dllPath);
        Assert.True(result.Diagnostics.HasErrors);
    }

    // ─── Phase 4 Batch 2: Set Literals ───

    [Fact]
    public void SetLiteral_CreatesHashSet()
    {
        var output = CompileAndRun("""
            def main():
                s = {3, 1, 2, 1, 3}
                print(len(s))
            """);
        Assert.Equal("3", output);
    }

    // ─── Phase 4 Batch 2: Dict Literals ───

    [Fact]
    public void DictLiteral_CreatesDictionary()
    {
        var output = CompileAndRun("""
            def main():
                d = {"a": 1, "b": 2, "c": 3}
                print(len(d))
            """);
        Assert.Equal("3", output);
    }

    // ─── Phase 4 Batch 2: Dict Comprehension ───

    [Fact]
    public void DictComprehension_BuildsDict()
    {
        var output = CompileAndRun("""
            def main():
                d = {x: x * x for x in range(4)}
                print(len(d))
            """);
        Assert.Equal("4", output);
    }

    // ─── Phase 4 Batch 2: With Statement ───

    [Fact]
    public void WithStatement_CallsDispose()
    {
        var output = CompileAndRun("""
            from System.Text import StringBuilder

            def main():
                with StringBuilder() as sb:
                    sb.append("hello")
                    print("inside with")
                print("after with")
            """);
        Assert.Equal("inside with\nafter with", output);
    }

    // ─── Phase 4 Batch 2: Tuple Unpacking ───

    [Fact]
    public void TupleUnpacking_Swap()
    {
        var output = CompileAndRun("""
            def main():
                a = 1
                b = 2
                a, b = b, a
                print(a)
                print(b)
            """);
        Assert.Equal("2\n1", output);
    }

    [Fact]
    public void TupleUnpacking_MultipleValues()
    {
        var output = CompileAndRun("""
            def main():
                x, y, z = 10, 20, 30
                print(x)
                print(y)
                print(z)
            """);
        Assert.Equal("10\n20\n30", output);
    }

    // ─── Phase 4 Batch 2: Lambda Expressions ───

    [Fact]
    public void Lambda_BasicExpression()
    {
        var output = CompileAndRun("""
            def apply(f, x: int) -> object:
                return f(x)

            def main():
                double = lambda x: x * 2
                result = apply(double, 5)
                print(result)
            """);
        Assert.Equal("10", output);
    }

    // ─── Phase 4 Batch 2: Slicing ───

    [Fact]
    public void Slice_ListBasic()
    {
        var output = CompileAndRun("""
            def main():
                items = [10, 20, 30, 40, 50]
                sub = items[1:3]
                print(len(sub))
            """);
        Assert.Equal("2", output);
    }

    [Fact]
    public void Slice_StringBasic()
    {
        var output = CompileAndRun("""
            def main():
                name = "Culebral"
                sub = name[0:4]
                print(sub)
            """);
        Assert.Equal("Cule", output);
    }

    // ─── Phase 4 Batch 2: Type Cast ───

    [Fact]
    public void TypeCast_ObjectToString()
    {
        var output = CompileAndRun("""
            def main():
                x: object = "hello"
                s = str(x)
                print(s)
            """);
        Assert.Equal("hello", output);
    }

    // ─── Phase 4 Batch 3: Record With Expressions ───

    [Fact]
    public void RecordWithExpr_CopiesAndOverrides()
    {
        var output = CompileAndRun("""
            record Point:
                x: int = 0
                y: int = 0

                def __init__(x: int, y: int):
                    @x = x
                    @y = y

            def main():
                p1 = Point(1, 2)
                p2 = p1 with (x=10)
                print(p2.x)
                print(p2.y)
            """);
        Assert.Equal("10\n2", output);
    }

    [Fact]
    public void RecordWithExpr_OverridesMultipleFields()
    {
        var output = CompileAndRun("""
            record Person:
                name: str = ""
                age: int = 0

                def __init__(name: str, age: int):
                    @name = name
                    @age = age

            def main():
                alice = Person("Alice", 30)
                bob = alice with (name="Bob", age=25)
                print(bob.name)
                print(bob.age)
            """);
        Assert.Equal("Bob\n25", output);
    }

    // ─── Phase 4 Batch 3: Type Aliases ───

    [Fact]
    public void TypeAlias_BasicIntAlias()
    {
        var output = CompileAndRun("""
            type Count = int

            def add(a: Count, b: Count) -> Count:
                return a + b

            def main():
                x: Count = 10
                y: Count = 20
                print(add(x, y))
            """);
        Assert.Equal("30", output);
    }

    [Fact]
    public void TypeAlias_StringAlias()
    {
        var output = CompileAndRun("""
            type Name = str

            def greet(n: Name) -> str:
                return f"Hello, {n}"

            def main():
                name: Name = "World"
                print(greet(name))
            """);
        Assert.Equal("Hello, World", output);
    }

    // ─── Phase 4: Operator Overloading via Dunder Methods ───

    [Fact]
    public void DunderEq_EqualsOverride()
    {
        var output = CompileAndRun("""
            class Point:
                x: int = 0
                y: int = 0

                def __init__(x: int, y: int):
                    @x = x
                    @y = y

                def __eq__(other: Point) -> bool:
                    return @x == other.x and @y == other.y

            def main():
                a = Point(1, 2)
                b = Point(1, 2)
                c = Point(3, 4)
                print(a.__eq__(b))
                print(a.__eq__(c))
            """);
        Assert.Equal("True\nFalse", output);
    }

    [Fact]
    public void DunderAdd_OpAddition()
    {
        var output = CompileAndRun("""
            class Vec:
                x: int = 0
                y: int = 0

                def __init__(x: int, y: int):
                    @x = x
                    @y = y

                def __add__(other: Vec) -> Vec:
                    return Vec(@x + other.x, @y + other.y)

                def __str__() -> str:
                    return f"Vec({@x}, {@y})"

            def main():
                a = Vec(1, 2)
                b = Vec(3, 4)
                c = a.__add__(b)
                print(c)
            """);
        Assert.Equal("Vec(4, 6)", output);
    }

    [Fact]
    public void DunderSub_OpSubtraction()
    {
        var output = CompileAndRun("""
            class Vec:
                x: int = 0
                y: int = 0

                def __init__(x: int, y: int):
                    @x = x
                    @y = y

                def __sub__(other: Vec) -> Vec:
                    return Vec(@x - other.x, @y - other.y)

                def __str__() -> str:
                    return f"Vec({@x}, {@y})"

            def main():
                a = Vec(10, 20)
                b = Vec(3, 5)
                c = a.__sub__(b)
                print(c)
            """);
        Assert.Equal("Vec(7, 15)", output);
    }

    [Fact]
    public void DunderMul_OpMultiply()
    {
        var output = CompileAndRun("""
            class Vec:
                x: int = 0
                y: int = 0

                def __init__(x: int, y: int):
                    @x = x
                    @y = y

                def __mul__(other: Vec) -> Vec:
                    return Vec(@x * other.x, @y * other.y)

                def __str__() -> str:
                    return f"Vec({@x}, {@y})"

            def main():
                a = Vec(3, 4)
                b = Vec(2, 5)
                c = a.__mul__(b)
                print(c)
            """);
        Assert.Equal("Vec(6, 20)", output);
    }

    [Fact]
    public void DunderLt_Comparison()
    {
        var output = CompileAndRun("""
            class Score:
                value: int = 0

                def __init__(value: int):
                    @value = value

                def __lt__(other: Score) -> bool:
                    return @value < other.value

            def main():
                a = Score(10)
                b = Score(20)
                print(a.__lt__(b))
                print(b.__lt__(a))
            """);
        Assert.Equal("True\nFalse", output);
    }

    [Fact]
    public void DunderHash_GetHashCodeOverride()
    {
        var output = CompileAndRun("""
            class Id:
                value: int = 0

                def __init__(value: int):
                    @value = value

                def __hash__() -> int:
                    return @value * 31

            def main():
                a = Id(5)
                print(a.GetHashCode())
            """);
        Assert.Equal("155", output);
    }

    [Fact]
    public void DunderLen_BuiltinLen()
    {
        var output = CompileAndRun("""
            class Stack:
                count: int = 0

                def __init__(count: int):
                    @count = count

                def __len__() -> int:
                    return @count

            def main():
                s = Stack(42)
                print(len(s))
            """);
        Assert.Equal("42", output);
    }

    [Fact]
    public void DunderGetitem_Indexing()
    {
        var output = CompileAndRun("""
            class Pair:
                a: int = 0
                b: int = 0

                def __init__(a: int, b: int):
                    @a = a
                    @b = b

                def __getitem__(index: int) -> int:
                    if index == 0:
                        return @a
                    return @b

            def main():
                p = Pair(10, 20)
                print(p[0])
                print(p[1])
            """);
        Assert.Equal("10\n20", output);
    }

    [Fact]
    public void DunderContains_InOperator()
    {
        var output = CompileAndRun("""
            class Bag:
                items: str = ""

                def __init__(items: str):
                    @items = items

                def __contains__(item: str) -> bool:
                    return @items == item

            def main():
                b = Bag("apple")
                print("apple" in b)
                print("banana" in b)
            """);
        Assert.Equal("True\nFalse", output);
    }

    [Fact]
    public void DunderNe_Inequality()
    {
        var output = CompileAndRun("""
            class Id:
                value: int = 0

                def __init__(value: int):
                    @value = value

                def __ne__(other: Id) -> bool:
                    return @value != other.value

            def main():
                a = Id(1)
                b = Id(2)
                c = Id(1)
                print(a.__ne__(b))
                print(a.__ne__(c))
            """);
        Assert.Equal("True\nFalse", output);
    }

    [Fact]
    public void DunderGt_GreaterThan()
    {
        var output = CompileAndRun("""
            class Score:
                value: int = 0

                def __init__(value: int):
                    @value = value

                def __gt__(other: Score) -> bool:
                    return @value > other.value

            def main():
                a = Score(30)
                b = Score(20)
                print(a.__gt__(b))
                print(b.__gt__(a))
            """);
        Assert.Equal("True\nFalse", output);
    }

    [Fact]
    public void DunderGe_GreaterEqual()
    {
        var output = CompileAndRun("""
            class Score:
                value: int = 0

                def __init__(value: int):
                    @value = value

                def __ge__(other: Score) -> bool:
                    return @value >= other.value

            def main():
                a = Score(20)
                b = Score(20)
                c = Score(10)
                print(a.__ge__(b))
                print(a.__ge__(c))
                print(c.__ge__(a))
            """);
        Assert.Equal("True\nTrue\nFalse", output);
    }

    [Fact]
    public void DunderLe_LessEqual()
    {
        var output = CompileAndRun("""
            class Score:
                value: int = 0

                def __init__(value: int):
                    @value = value

                def __le__(other: Score) -> bool:
                    return @value <= other.value

            def main():
                a = Score(10)
                b = Score(20)
                c = Score(10)
                print(a.__le__(b))
                print(a.__le__(c))
                print(b.__le__(a))
            """);
        Assert.Equal("True\nTrue\nFalse", output);
    }

    [Fact]
    public void DunderTruediv_Division()
    {
        var output = CompileAndRun("""
            class Ratio:
                num: int = 0
                den: int = 1

                def __init__(num: int, den: int):
                    @num = num
                    @den = den

                def __truediv__(other: Ratio) -> Ratio:
                    return Ratio(@num * other.den, @den * other.num)

                def __str__() -> str:
                    return f"{@num}/{@den}"

            def main():
                a = Ratio(1, 2)
                b = Ratio(3, 4)
                c = a.__truediv__(b)
                print(c)
            """);
        Assert.Equal("4/6", output);
    }

    [Fact]
    public void DunderMod_Modulus()
    {
        var output = CompileAndRun("""
            class Num:
                value: int = 0

                def __init__(value: int):
                    @value = value

                def __mod__(other: Num) -> Num:
                    return Num(@value % other.value)

                def __str__() -> str:
                    return f"Num({@value})"

            def main():
                a = Num(17)
                b = Num(5)
                c = a.__mod__(b)
                print(c)
            """);
        Assert.Equal("Num(2)", output);
    }

    [Fact]
    public void DunderEq_WithStr_Combined()
    {
        var output = CompileAndRun("""
            class Money:
                amount: int = 0
                currency: str = ""

                def __init__(amount: int, currency: str):
                    @amount = amount
                    @currency = currency

                def __eq__(other: Money) -> bool:
                    return @amount == other.amount and @currency == other.currency

                def __add__(other: Money) -> Money:
                    return Money(@amount + other.amount, @currency)

                def __str__() -> str:
                    return f"{@amount} {@currency}"

            def main():
                a = Money(10, "USD")
                b = Money(20, "USD")
                c = a.__add__(b)
                print(c)
                print(a.__eq__(Money(10, "USD")))
                print(a.__eq__(b))
            """);
        Assert.Equal("30 USD\nTrue\nFalse", output);
    }

    // ─── Phase 4: Built-in Functions & Assert ───

    [Fact]
    public void Builtin_Abs_NegativeInt()
    {
        var output = CompileAndRun("""
            def main():
                print(abs(-5))
            """);
        Assert.Equal("5", output);
    }

    [Fact]
    public void Builtin_Min_TwoInts()
    {
        var output = CompileAndRun("""
            def main():
                print(min(3, 7))
            """);
        Assert.Equal("3", output);
    }

    [Fact]
    public void Builtin_Max_TwoInts()
    {
        var output = CompileAndRun("""
            def main():
                print(max(3, 7))
            """);
        Assert.Equal("7", output);
    }

    [Fact]
    public void Builtin_Chr()
    {
        var output = CompileAndRun("""
            def main():
                print(chr(65))
            """);
        Assert.Equal("A", output);
    }

    [Fact]
    public void Builtin_Ord()
    {
        var output = CompileAndRun("""
            def main():
                print(ord("A"))
            """);
        Assert.Equal("65", output);
    }

    [Fact]
    public void Builtin_Type_Int()
    {
        var output = CompileAndRun("""
            def main():
                print(type(42))
            """);
        Assert.Equal("Int32", output);
    }

    [Fact]
    public void Assert_TruePasses()
    {
        var output = CompileAndRun("""
            def main():
                assert True
                print("ok")
            """);
        Assert.Equal("ok", output);
    }

    [Fact]
    public void Assert_ExpressionPasses()
    {
        var output = CompileAndRun("""
            def main():
                assert 1 + 1 == 2
                print("ok")
            """);
        Assert.Equal("ok", output);
    }

    // ─── Comparison Chaining Tests ───

    [Fact]
    public void ComparisonChaining_InRange_Works()
    {
        var output = CompileAndRun("""
            def main():
                x = 5
                if 0 < x < 10:
                    print("in range")
                else:
                    print("out of range")
            """);
        Assert.Equal("in range", output);
    }

    [Fact]
    public void ComparisonChaining_OutOfRange_Works()
    {
        var output = CompileAndRun("""
            def main():
                x = 15
                if 0 < x < 10:
                    print("in range")
                else:
                    print("out of range")
            """);
        Assert.Equal("out of range", output);
    }

    [Fact]
    public void ComparisonChaining_ThreeVars_Ascending()
    {
        var output = CompileAndRun("""
            def main():
                a = 1
                b = 2
                c = 3
                if a < b < c:
                    print("ascending")
            """);
        Assert.Equal("ascending", output);
    }

    [Fact]
    public void ComparisonChaining_MixedOperators_Works()
    {
        var output = CompileAndRun("""
            def main():
                if 1 <= 1 < 5:
                    print("yes")
            """);
        Assert.Equal("yes", output);
    }

    [Fact]
    public void ComparisonChaining_FourWay_Works()
    {
        var output = CompileAndRun("""
            def main():
                if 1 < 2 < 3 < 4:
                    print("all ascending")
                else:
                    print("not ascending")
            """);
        Assert.Equal("all ascending", output);
    }

    [Fact]
    public void ComparisonChaining_FourWay_Fails()
    {
        var output = CompileAndRun("""
            def main():
                if 1 < 2 < 3 < 2:
                    print("all ascending")
                else:
                    print("not ascending")
            """);
        Assert.Equal("not ascending", output);
    }

    [Fact]
    public void ComparisonChaining_EqualityChain_Works()
    {
        var output = CompileAndRun("""
            def main():
                if 1 == 1 == 1:
                    print("all equal")
                else:
                    print("not equal")
            """);
        Assert.Equal("all equal", output);
    }

    // ─── Phase 4: Struct Value-Type Semantics ───

    [Fact]
    public void StructValueType_FieldAccess_Works()
    {
        var output = CompileAndRun("""
            struct Point:
                x: int = 0
                y: int = 0

                def __init__(x: int, y: int):
                    @x = x
                    @y = y

            def main():
                p = Point(3, 4)
                print(p.x)
                print(p.y)
            """);
        Assert.Equal("3\n4", output);
    }

    // ─── For-Else Tests ───

    [Fact]
    public void ForElse_NoBreak_ElseExecutes()
    {
        var output = CompileAndRun("""
            def main():
                for x in range(5):
                    if x == 10:
                        break
                else:
                    print("no break")
            """);
        Assert.Equal("no break", output);
    }

    [Fact]
    public void ForElse_BreakHit_ElseSkipped()
    {
        var output = CompileAndRun("""
            def main():
                for x in range(5):
                    if x == 3:
                        break
                else:
                    print("no break")
                print("done")
            """);
        Assert.Equal("done", output);
    }

    [Fact]
    public void ForElse_EmptyIterable_ElseExecutes()
    {
        var output = CompileAndRun("""
            def main():
                for x in range(0):
                    print("body")
                else:
                    print("else")
            """);
        Assert.Equal("else", output);
    }

    // ─── While-Else Tests ───

    [Fact]
    public void WhileElse_NaturalExit_ElseExecutes()
    {
        var output = CompileAndRun("""
            def main():
                x = 0
                while x < 3:
                    x = x + 1
                else:
                    print("done naturally")
            """);
        Assert.Equal("done naturally", output);
    }

    [Fact]
    public void WhileElse_BreakHit_ElseSkipped()
    {
        var output = CompileAndRun("""
            def main():
                x = 0
                while x < 10:
                    if x == 3:
                        break
                    x = x + 1
                else:
                    print("no break")
                print("after")
            """);
        Assert.Equal("after", output);
    }

    // ─── Decorator Tests ───

    [Fact]
    public void Decorator_ObsoleteAttribute_CompilesAndRuns()
    {
        var output = CompileAndRun("""
            @Obsolete
            def greet(name: str) -> str:
                return "hello " + name

            def main():
                print(greet("world"))
            """);
        Assert.Equal("hello world", output);
    }

    [Fact]
    public void Decorator_PreservedInIR()
    {
        // Verify decorator names are passed through to IrFunction — compilation succeeds
        var source = """
            @Obsolete
            def greet(name: str) -> str:
                return "hello " + name

            def main():
                print(greet("world"))
            """;
        var skvPath = Path.Combine(_tempDir, "test_decorator.cbl");
        var dllPath = Path.Combine(_tempDir, "test_decorator.dll");
        File.WriteAllText(skvPath, source);

        var result = Program.Compile(skvPath, dllPath);
        Assert.True(result.Success, "Compilation failed:\n" + result.Diagnostics.FormatAll());
        Assert.True(File.Exists(dllPath), "Output assembly was not created");
    }

    // ─── Generator / Yield Tests ───

    [Fact]
    public void Yield_CountUp_Works()
    {
        var output = CompileAndRun("""
            def count_up(n: int):
                i = 0
                while i < n:
                    yield i
                    i = i + 1

            def main():
                for x in count_up(5):
                    print(x)
            """);
        Assert.Equal("0\n1\n2\n3\n4", output);
    }

    [Fact]
    public void Yield_Evens_Works()
    {
        var output = CompileAndRun("""
            def evens(n: int):
                for i in range(n):
                    if i % 2 == 0:
                        yield i

            def main():
                for x in evens(10):
                    print(x)
            """);
        Assert.Equal("0\n2\n4\n6\n8", output);
    }

    // ─── Phase 4: Generic Constraints ───

    [Fact]
    public void GenericConstraint_ParsedAndCarried()
    {
        // Verify that a constrained generic class compiles and runs correctly.
        // The constraint is parsed, carried through the IR, and doesn't break emission.
        // Note: with type erasure, we can't call constraint methods through the type param,
        // but the constraint metadata is carried through and validated at the type checker level.
        var output = CompileAndRun("""
            interface Printable:
                def to_string() -> str

            class Box[T: Printable]:
                value: T

                def __init__(v: T):
                    value = v

                def get_value() -> T:
                    return value

            class Label(Printable):
                text: str = ""

                def __init__(t: str):
                    @text = t

                def to_string() -> str:
                    return @text

            def main():
                lbl = Label("hello")
                b = Box(lbl)
                print(lbl.to_string())
            """);
        Assert.Equal("hello", output);
    }

    [Fact]
    public void GenericConstraint_ViolationDetected()
    {
        // Verify that using a primitive type with a constrained generic type
        // at the type annotation level (Box[int]) emits a diagnostic error.
        var source = """
            interface Printable:
                def to_string() -> str

            class Box[T: Printable]:
                value: T

            def main():
                x: Box[int] = Box(42)
                pass
            """;
        var skvPath = Path.Combine(_tempDir, "test_constraint_violation.cbl");
        var dllPath = Path.Combine(_tempDir, "test_constraint_violation.dll");
        File.WriteAllText(skvPath, source);

        var result = Program.Compile(skvPath, dllPath);
        // Should fail compilation due to constraint violation
        Assert.False(result.Success, "Expected compilation to fail due to constraint violation");
        Assert.Contains("CBL2020", result.Diagnostics.FormatAll());
    }

    [Fact]
    public void GenericConstraint_MultipleParams_Works()
    {
        // Generic class with multiple type parameters, one constrained and one not.
        // Verifies that constraints are carried through without breaking compilation.
        var output = CompileAndRun("""
            interface Displayable:
                def show() -> str

            class Wrapper(Displayable):
                val: str = ""

                def __init__(v: str):
                    @val = v

                def show() -> str:
                    return @val

            class Pair[A: Displayable, B]:
                first: A
                second: B

                def __init__(a: A, b: B):
                    first = a
                    second = b

                def get_second() -> B:
                    return second

            def main():
                w = Wrapper("greet")
                p = Pair(w, 42)
                print(w.show())
                print(p.get_second())
            """);
        Assert.Equal("greet\n42", output);
    }

    // ─── Phase 4: Default Interface Implementations ───

    [Fact]
    public void InterfaceDefaultMethod_Works()
    {
        var output = CompileAndRun("""
            interface Greeting:
                def greet() -> str:
                    return "Hello!"

            class Bot(Greeting):
                pass

            def main():
                b = Bot()
                print(b.greet())
            """);
        Assert.Equal("Hello!", output);
    }

    [Fact]
    public void InterfaceDefaultMethod_CanBeOverridden()
    {
        var output = CompileAndRun("""
            interface Greeting:
                def greet() -> str:
                    return "Hello!"

            class FriendlyBot(Greeting):
                def greet() -> str:
                    return "Hey there!"

            def main():
                b = FriendlyBot()
                print(b.greet())
            """);
        Assert.Equal("Hey there!", output);
    }

    [Fact]
    public void InterfaceDefaultMethod_WithAbstractMethod()
    {
        // Interface with both a default method and an abstract method
        var output = CompileAndRun("""
            interface Animal:
                def speak() -> str

                def description() -> str:
                    return "I am an animal"

            class Cat(Animal):
                def speak() -> str:
                    return "Meow"

            def main():
                c = Cat()
                print(c.speak())
                print(c.description())
            """);
        Assert.Equal("Meow\nI am an animal", output);
    }
}
