using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Lexer;
using Culebral.Compiler.Parser;

namespace Culebral.Compiler.Tests;

public class ParserTests
{
    private static CompilationUnit Parse(string source)
    {
        var diagnostics = new DiagnosticBag();
        var lexer = new CulebralLexer(source, "<test>", diagnostics);
        var tokens = lexer.Tokenize();
        Assert.False(diagnostics.HasErrors, "Lexer errors: " + diagnostics.FormatAll());

        var parser = new CulebralParser(tokens, diagnostics);
        var ast = parser.ParseCompilationUnit();
        Assert.False(diagnostics.HasErrors, "Parser errors: " + diagnostics.FormatAll());
        return ast;
    }

    [Fact]
    public void SimpleFunction_Parses()
    {
        var ast = Parse("def main():\n    print(\"hello\")\n");
        Assert.Single(ast.Statements);
        var func = Assert.IsType<FunctionDef>(ast.Statements[0]);
        Assert.Equal("main", func.Name);
        Assert.Empty(func.Parameters);
        Assert.Null(func.ReturnType);
        Assert.Single(func.Body.Statements);
    }

    [Fact]
    public void FunctionWithParams_Parses()
    {
        var ast = Parse("def greet(name: str, times: int) -> str:\n    pass\n");
        var func = Assert.IsType<FunctionDef>(ast.Statements[0]);
        Assert.Equal("greet", func.Name);
        Assert.Equal(2, func.Parameters.Count);
        Assert.Equal("name", func.Parameters[0].Name);
        Assert.Equal("times", func.Parameters[1].Name);
        Assert.NotNull(func.ReturnType);
        Assert.IsType<SimpleType>(func.ReturnType);
    }

    [Fact]
    public void AsyncFunction_Parses()
    {
        var ast = Parse("async def fetch(url: str) -> str:\n    pass\n");
        var func = Assert.IsType<FunctionDef>(ast.Statements[0]);
        Assert.True(func.IsAsync);
        Assert.Equal("fetch", func.Name);
    }

    [Fact]
    public void IfElifElse_Parses()
    {
        var source = "if x > 0:\n    pass\nelif x < 0:\n    pass\nelse:\n    pass\n";
        var ast = Parse(source);
        var ifStmt = Assert.IsType<IfStatement>(ast.Statements[0]);
        Assert.Single(ifStmt.Elifs);
        Assert.NotNull(ifStmt.ElseBody);
    }

    [Fact]
    public void WhileLoop_Parses()
    {
        var ast = Parse("while x > 0:\n    x = x - 1\n");
        var whileStmt = Assert.IsType<WhileStatement>(ast.Statements[0]);
        Assert.IsType<BinaryExpr>(whileStmt.Condition);
    }

    [Fact]
    public void ForLoop_Parses()
    {
        var ast = Parse("for i in range(10):\n    print(i)\n");
        var forStmt = Assert.IsType<ForStatement>(ast.Statements[0]);
        Assert.Equal("i", forStmt.Variable);
    }

    [Fact]
    public void ClassDef_Parses()
    {
        var source = "class Counter:\n    count: int = 0\n\n    def increment() -> int:\n        count += 1\n        return count\n";
        var ast = Parse(source);
        var cls = Assert.IsType<ClassDef>(ast.Statements[0]);
        Assert.Equal("Counter", cls.Name);
        Assert.True(cls.Members.Count >= 2); // field + method
    }

    [Fact]
    public void ClassWithBases_Parses()
    {
        var source = "class Circle(Drawable, Printable):\n    pass\n";
        var ast = Parse(source);
        var cls = Assert.IsType<ClassDef>(ast.Statements[0]);
        Assert.Equal(2, cls.Bases.Count);
    }

    [Fact]
    public void StructDef_Parses()
    {
        var source = "struct Point:\n    x: float\n    y: float\n";
        var ast = Parse(source);
        var strct = Assert.IsType<StructDef>(ast.Statements[0]);
        Assert.Equal("Point", strct.Name);
    }

    [Fact]
    public void RecordDef_Parses()
    {
        var source = "record User:\n    name: str\n    email: str\n    age: int\n";
        var ast = Parse(source);
        var rec = Assert.IsType<RecordDef>(ast.Statements[0]);
        Assert.Equal("User", rec.Name);
    }

    [Fact]
    public void EnumDef_Parses()
    {
        var source = "enum Shape:\n    Circle(radius: float)\n    Rectangle(width: float, height: float)\n    Point\n";
        var ast = Parse(source);
        var enumDef = Assert.IsType<EnumDef>(ast.Statements[0]);
        Assert.Equal("Shape", enumDef.Name);
        Assert.Equal(3, enumDef.Variants.Count);
        Assert.NotNull(enumDef.Variants[0].Fields);
        Assert.Null(enumDef.Variants[2].Fields);
    }

    [Fact]
    public void InterfaceDef_Parses()
    {
        var source = "interface Drawable:\n    def draw(canvas: Canvas) -> None\n";
        var ast = Parse(source);
        var iface = Assert.IsType<InterfaceDef>(ast.Statements[0]);
        Assert.Equal("Drawable", iface.Name);
    }

    [Fact]
    public void Import_Parses()
    {
        var ast = Parse("import System.IO as io\n");
        var imp = Assert.IsType<ImportStatement>(ast.Statements[0]);
        Assert.Equal("System.IO", imp.ModulePath);
        Assert.Equal("io", imp.Alias);
    }

    [Fact]
    public void FromImport_Parses()
    {
        var ast = Parse("from System.Net.Http import HttpClient, HttpResponseMessage\n");
        var imp = Assert.IsType<FromImportStatement>(ast.Statements[0]);
        Assert.Equal("System.Net.Http", imp.ModulePath);
        Assert.Equal(2, imp.Names.Count);
    }

    [Fact]
    public void AnnotatedAssignment_Parses()
    {
        var ast = Parse("x: float = 42\n");
        var stmt = Assert.IsType<AnnotatedAssignment>(ast.Statements[0]);
        Assert.Equal("x", stmt.Name);
        Assert.IsType<SimpleType>(stmt.TypeAnnotation);
        Assert.NotNull(stmt.Value);
    }

    [Fact]
    public void NullableType_Parses()
    {
        var ast = Parse("def find(id: int) -> User?:\n    pass\n");
        var func = Assert.IsType<FunctionDef>(ast.Statements[0]);
        Assert.IsType<NullableType>(func.ReturnType);
    }

    [Fact]
    public void GenericType_Parses()
    {
        var ast = Parse("x: list[int] = []\n");
        var stmt = Assert.IsType<AnnotatedAssignment>(ast.Statements[0]);
        var genType = Assert.IsType<GenericType>(stmt.TypeAnnotation);
        Assert.Equal("list", genType.Name);
        Assert.Single(genType.TypeArgs);
    }

    [Fact]
    public void ListExpression_Parses()
    {
        var ast = Parse("x = [1, 2, 3]\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        var list = Assert.IsType<ListExpr>(assign.Value);
        Assert.Equal(3, list.Elements.Count);
    }

    [Fact]
    public void DictExpression_Parses()
    {
        var ast = Parse("x = {\"a\": 1, \"b\": 2}\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        var dict = Assert.IsType<DictExpr>(assign.Value);
        Assert.Equal(2, dict.Entries.Count);
    }

    [Fact]
    public void ListComprehension_Parses()
    {
        var ast = Parse("x = [i * 2 for i in range(10) if i > 3]\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        Assert.IsType<ListComprehension>(assign.Value);
    }

    [Fact]
    public void Lambda_Parses()
    {
        var ast = Parse("f = lambda x: x * 2\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        var lambda = Assert.IsType<LambdaExpr>(assign.Value);
        Assert.Single(lambda.Parameters);
    }

    [Fact]
    public void ConditionalExpr_Parses()
    {
        var ast = Parse("x = a if condition else b\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        Assert.IsType<ConditionalExpr>(assign.Value);
    }

    [Fact]
    public void MatchStatement_Parses()
    {
        var source = "match x:\n    case 1:\n        pass\n    case _:\n        pass\n";
        var ast = Parse(source);
        var match = Assert.IsType<MatchStatement>(ast.Statements[0]);
        Assert.Equal(2, match.Cases.Count);
    }

    [Fact]
    public void TryExcept_Parses()
    {
        var source = "try:\n    pass\nexcept ValueError as e:\n    pass\nfinally:\n    pass\n";
        var ast = Parse(source);
        var tryStmt = Assert.IsType<TryStatement>(ast.Statements[0]);
        Assert.Single(tryStmt.ExceptClauses);
        Assert.NotNull(tryStmt.FinallyBody);
    }

    [Fact]
    public void WithStatement_Parses()
    {
        var source = "with open(\"file.txt\") as f:\n    pass\n";
        var ast = Parse(source);
        var withStmt = Assert.IsType<WithStatement>(ast.Statements[0]);
        Assert.Single(withStmt.Items);
        Assert.Equal("f", withStmt.Items[0].Variable);
    }

    [Fact]
    public void MemberAccess_Parses()
    {
        var ast = Parse("x = obj.method()\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        var call = Assert.IsType<CallExpr>(assign.Value);
        Assert.IsType<MemberAccessExpr>(call.Callee);
    }

    [Fact]
    public void IndexExpression_Parses()
    {
        var ast = Parse("x = items[0]\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        Assert.IsType<IndexExpr>(assign.Value);
    }

    [Fact]
    public void FString_Parses()
    {
        var ast = Parse("x = f\"Hello, {name}!\"\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        Assert.IsType<FStringExpr>(assign.Value);
    }

    [Fact]
    public void ReturnStatement_Parses()
    {
        var source = "def f() -> int:\n    return 42\n";
        var ast = Parse(source);
        var func = Assert.IsType<FunctionDef>(ast.Statements[0]);
        var ret = Assert.IsType<ReturnStatement>(func.Body.Statements[0]);
        Assert.NotNull(ret.Value);
    }

    [Fact]
    public void BreakContinuePass_Parse()
    {
        var source = "while True:\n    break\n    continue\n    pass\n";
        var ast = Parse(source);
        var whileStmt = Assert.IsType<WhileStatement>(ast.Statements[0]);
        Assert.IsType<BreakStatement>(whileStmt.Body.Statements[0]);
        Assert.IsType<ContinueStatement>(whileStmt.Body.Statements[1]);
        Assert.IsType<PassStatement>(whileStmt.Body.Statements[2]);
    }

    [Fact]
    public void WhenTarget_Parses()
    {
        var source = "when target == \"net\":\n    pass\n";
        var ast = Parse(source);
        var when = Assert.IsType<WhenStatement>(ast.Statements[0]);
        Assert.Equal("target", when.Target);
        Assert.Equal("net", when.Value);
    }

    [Fact]
    public void TupleUnpacking_SwapTwoVariables()
    {
        var ast = Parse("a, b = b, a\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        var target = Assert.IsType<TupleExpr>(assign.Target);
        Assert.Equal(2, target.Elements.Count);
        Assert.Equal("a", Assert.IsType<IdentifierExpr>(target.Elements[0]).Name);
        Assert.Equal("b", Assert.IsType<IdentifierExpr>(target.Elements[1]).Name);
        var value = Assert.IsType<TupleExpr>(assign.Value);
        Assert.Equal(2, value.Elements.Count);
        Assert.Equal("b", Assert.IsType<IdentifierExpr>(value.Elements[0]).Name);
        Assert.Equal("a", Assert.IsType<IdentifierExpr>(value.Elements[1]).Name);
    }

    [Fact]
    public void TupleUnpacking_ThreeVariables()
    {
        var ast = Parse("x, y, z = 10, 20, 30\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        var target = Assert.IsType<TupleExpr>(assign.Target);
        Assert.Equal(3, target.Elements.Count);
        Assert.Equal("x", Assert.IsType<IdentifierExpr>(target.Elements[0]).Name);
        Assert.Equal("y", Assert.IsType<IdentifierExpr>(target.Elements[1]).Name);
        Assert.Equal("z", Assert.IsType<IdentifierExpr>(target.Elements[2]).Name);
        var value = Assert.IsType<TupleExpr>(assign.Value);
        Assert.Equal(3, value.Elements.Count);
    }

    [Fact]
    public void TupleUnpacking_SingleRhsValue()
    {
        var ast = Parse("a, b = get_pair()\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        var target = Assert.IsType<TupleExpr>(assign.Target);
        Assert.Equal(2, target.Elements.Count);
        Assert.IsType<CallExpr>(assign.Value);
    }

    [Fact]
    public void TupleUnpacking_DoesNotBreakFunctionCalls()
    {
        var ast = Parse("print(a, b)\n");
        var exprStmt = Assert.IsType<ExpressionStatement>(ast.Statements[0]);
        var call = Assert.IsType<CallExpr>(exprStmt.Expr);
        Assert.Equal(2, call.Arguments.Count);
    }

    [Fact]
    public void TupleUnpacking_DoesNotBreakListLiterals()
    {
        var ast = Parse("x = [1, 2, 3]\n");
        var assign = Assert.IsType<AssignmentStatement>(ast.Statements[0]);
        var list = Assert.IsType<ListExpr>(assign.Value);
        Assert.Equal(3, list.Elements.Count);
    }

    [Fact]
    public void ForElse_Parses()
    {
        var ast = Parse("for i in range(5):\n    pass\nelse:\n    print(\"done\")\n");
        var forStmt = Assert.IsType<ForStatement>(ast.Statements[0]);
        Assert.Equal("i", forStmt.Variable);
        Assert.NotNull(forStmt.ElseBody);
        Assert.Single(forStmt.ElseBody.Statements);
    }

    [Fact]
    public void WhileElse_Parses()
    {
        var ast = Parse("while True:\n    break\nelse:\n    print(\"done\")\n");
        var whileStmt = Assert.IsType<WhileStatement>(ast.Statements[0]);
        Assert.NotNull(whileStmt.ElseBody);
        Assert.Single(whileStmt.ElseBody.Statements);
    }

    [Fact]
    public void ForWithoutElse_HasNullElseBody()
    {
        var ast = Parse("for i in range(5):\n    pass\n");
        var forStmt = Assert.IsType<ForStatement>(ast.Statements[0]);
        Assert.Null(forStmt.ElseBody);
    }

    [Fact]
    public void WhileWithoutElse_HasNullElseBody()
    {
        var ast = Parse("while True:\n    break\n");
        var whileStmt = Assert.IsType<WhileStatement>(ast.Statements[0]);
        Assert.Null(whileStmt.ElseBody);
    }
}
