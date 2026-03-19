using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Lexer;
using Culebral.Compiler.Parser;
using Culebral.Compiler.Semantics;

namespace Culebral.Compiler.Tests;

public class TypeCheckerTests
{
    private static (CompilationUnit Ast, TypeChecker Checker, DiagnosticBag Diagnostics) Check(string source)
    {
        var diagnostics = new DiagnosticBag();
        var lexer = new CulebralLexer(source, "<test>", diagnostics);
        var tokens = lexer.Tokenize();
        var parser = new CulebralParser(tokens, diagnostics);
        var ast = parser.ParseCompilationUnit();
        var checker = new TypeChecker(diagnostics);
        checker.Check(ast);
        return (ast, checker, diagnostics);
    }

    [Fact]
    public void FunctionDeclaration_RegistersSymbol()
    {
        var (_, checker, diagnostics) = Check("def greet(name: str) -> str:\n    return name\n");
        Assert.False(diagnostics.HasErrors, diagnostics.FormatAll());

        var symbol = checker.GlobalScope.Lookup("greet");
        Assert.NotNull(symbol);
        Assert.Equal(SymbolKind.Function, symbol.Kind);
    }

    [Fact]
    public void TypeAnnotation_Resolves()
    {
        var (_, checker, diagnostics) = Check("def f(x: int) -> float:\n    return 1.0\n");
        Assert.False(diagnostics.HasErrors, diagnostics.FormatAll());

        var symbol = checker.GlobalScope.Lookup("f");
        Assert.NotNull(symbol);
        var funcType = Assert.IsType<FunctionType>(symbol.Type);
        Assert.Equal(PrimitiveType.Int, funcType.ParameterTypes[0]);
        Assert.Equal(PrimitiveType.Float, funcType.ReturnType);
    }

    [Fact]
    public void ClassDeclaration_RegistersSymbol()
    {
        var (_, checker, diagnostics) = Check("class Foo:\n    x: int = 0\n");
        Assert.False(diagnostics.HasErrors, diagnostics.FormatAll());

        var symbol = checker.GlobalScope.Lookup("Foo");
        Assert.NotNull(symbol);
        Assert.Equal(SymbolKind.Type, symbol.Kind);
    }

    [Fact]
    public void UndefinedVariable_ReportsError()
    {
        var (_, _, diagnostics) = Check("def main():\n    print(undefined_var)\n");
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.GetDiagnostics(), d => d.Message.Contains("undefined_var"));
    }

    [Fact]
    public void NullableType_Resolves()
    {
        var (_, checker, diagnostics) = Check("def f(x: int) -> int?:\n    return None\n");
        Assert.False(diagnostics.HasErrors, diagnostics.FormatAll());

        var symbol = checker.GlobalScope.Lookup("f");
        var funcType = Assert.IsType<FunctionType>(symbol!.Type);
        Assert.IsType<NullableCulebralType>(funcType.ReturnType);
    }

    [Fact]
    public void GenericType_Resolves()
    {
        var (_, checker, diagnostics) = Check("def f(items: list[int]) -> int:\n    return 0\n");
        Assert.False(diagnostics.HasErrors, diagnostics.FormatAll());

        var symbol = checker.GlobalScope.Lookup("f");
        var funcType = Assert.IsType<FunctionType>(symbol!.Type);
        Assert.IsType<GenericInstanceType>(funcType.ParameterTypes[0]);
    }

    [Fact]
    public void EnumDeclaration_RegistersVariants()
    {
        var (_, checker, diagnostics) = Check("enum Color:\n    Red\n    Green\n    Blue\n");
        Assert.False(diagnostics.HasErrors, diagnostics.FormatAll());

        var symbol = checker.GlobalScope.Lookup("Color");
        Assert.NotNull(symbol);
        Assert.Equal(SymbolKind.Type, symbol.Kind);
    }

    [Fact]
    public void InterfaceDeclaration_RegistersSymbol()
    {
        var (_, checker, diagnostics) = Check("interface Drawable:\n    def draw() -> None\n");
        Assert.False(diagnostics.HasErrors, diagnostics.FormatAll());

        var symbol = checker.GlobalScope.Lookup("Drawable");
        Assert.NotNull(symbol);
        Assert.Equal(SymbolKind.Type, symbol.Kind);
    }

    [Fact]
    public void BuiltinFunctions_AreAvailable()
    {
        var (_, checker, _) = Check("");
        Assert.NotNull(checker.GlobalScope.Lookup("print"));
        Assert.NotNull(checker.GlobalScope.Lookup("len"));
        Assert.NotNull(checker.GlobalScope.Lookup("range"));
    }

    [Fact]
    public void PrimitiveTypes_AreAvailable()
    {
        var (_, checker, _) = Check("");
        Assert.NotNull(checker.GlobalScope.Lookup("int"));
        Assert.NotNull(checker.GlobalScope.Lookup("float"));
        Assert.NotNull(checker.GlobalScope.Lookup("str"));
        Assert.NotNull(checker.GlobalScope.Lookup("bool"));
        Assert.NotNull(checker.GlobalScope.Lookup("object"));
    }

    [Fact]
    public void GenericConstraint_ViolationOnPrimitive_ReportsError()
    {
        var (_, _, diagnostics) = Check("""
            interface Printable:
                def to_string() -> str

            class Box[T: Printable]:
                value: T

            def main():
                x: Box[int] = Box(42)
            """);
        Assert.True(diagnostics.HasErrors, "Expected constraint violation error");
        Assert.Contains(diagnostics.GetDiagnostics(),
            d => d.Code == "LEB2020" && d.Message.Contains("Printable"));
    }

    [Fact]
    public void GenericConstraint_SatisfiedByClass_NoError()
    {
        var (_, _, diagnostics) = Check("""
            interface Printable:
                def to_string() -> str

            class Box[T: Printable]:
                value: T

            class Label(Printable):
                def to_string() -> str:
                    return "label"

            def main():
                x: Box[Label] = Box(Label())
            """);
        // No constraint violation — Label implements Printable
        Assert.DoesNotContain(diagnostics.GetDiagnostics(), d => d.Code == "LEB2020");
    }
}
