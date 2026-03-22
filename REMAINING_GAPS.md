# Culebral — Remaining Gaps Spec Sheet

> 11 items. Calibrated for ".NET-first" — C# semantics win where Python and .NET diverge.
> Numeric separators (`1_000_000`) already work. `//` and `%` use C# semantics. `char` iteration is correct.
> All 366 existing tests pass. No regressions allowed.

---

## GAP-1: Operator Syntax Dispatch for User-Defined Types

### The Bug

`a + b` where `a` is a user type with `__add__` crashes with `InvalidProgramException` because the compiler emits a raw CIL `add` opcode on two reference types. C# would call `op_Addition`. The operator methods are already being generated — they're just never called.

### Current Code — What Happens Now

**`Lowering.cs:1586-1682`** — The `BinaryExpr` case checks for list/string/dict special cases, then falls through to raw `IrBinaryOp`:

```csharp
// Line 1638: after dict merge check, NOTHING checks for user types. Falls to:
// Line 1640:
var isArithmetic = binOp is IrBinaryOpKind.Add or IrBinaryOpKind.Sub ...
// Line 1676:
_currentBlock!.Emit(new IrBinaryOp(binOp, effectiveType, expr.Span));
```

**`CilEmitter.cs:1660-1735`** — `EmitBinaryOp` only emits CIL opcodes:
```csharp
case IrBinaryOpKind.Add: il.Emit(OpCodes.Add); break;  // crashes on reference types
case IrBinaryOpKind.Equal: il.Emit(OpCodes.Ceq); break; // wrong for user types
```

**`CilEmitter.cs:272-296`** — The dunder mapping that generates the operator methods:
```csharp
["__add__"] = "op_Addition",
["__eq__"] = "op_Equality",
// ... 22 more mappings exist, methods ARE generated, just never called
```

### Working Pattern to Follow

**`Lowering.cs:2862-2874`** — `LowerInExpr` already does this correctly for `__contains__`:
```csharp
var collTypeName = collType?.DisplayName;
if (collTypeName is not null && _typeDefs.TryGetValue(collTypeName, out var collTypeDef) &&
    collTypeDef.Methods.Any(m => m.Name == "__contains__"))
{
    _currentBlock.Emit(new IrCallMethod(collTypeName, "Contains", 1, inExpr.Span));
}
```

This checks `_typeDefs` for the user type, checks if it has the dunder method, and emits `IrCallMethod` instead of a generic virtual call.

### Exact Fix

**File: `src/Culebral.Compiler/IR/Lowering.cs`**

**Step 1 — Add helper methods** (after `MapUnaryOp` at line 3509):

```csharp
private static string? BinaryOpToDunder(IrBinaryOpKind op) => op switch
{
    IrBinaryOpKind.Add => "__add__",
    IrBinaryOpKind.Sub => "__sub__",
    IrBinaryOpKind.Mul => "__mul__",
    IrBinaryOpKind.Div => "__truediv__",
    IrBinaryOpKind.IntDiv => "__floordiv__",
    IrBinaryOpKind.Mod => "__mod__",
    IrBinaryOpKind.Pow => "__pow__",
    IrBinaryOpKind.Equal => "__eq__",
    IrBinaryOpKind.NotEqual => "__ne__",
    IrBinaryOpKind.LessThan => "__lt__",
    IrBinaryOpKind.LessEqual => "__le__",
    IrBinaryOpKind.GreaterThan => "__gt__",
    IrBinaryOpKind.GreaterEqual => "__ge__",
    IrBinaryOpKind.BitAnd => "__and__",
    IrBinaryOpKind.BitOr => "__or__",
    IrBinaryOpKind.BitXor => "__xor__",
    IrBinaryOpKind.ShiftLeft => "__lshift__",
    IrBinaryOpKind.ShiftRight => "__rshift__",
    _ => null,
};

private static string? UnaryOpToDunder(Lexer.TokenKind op) => op switch
{
    Lexer.TokenKind.Minus => "__neg__",
    Lexer.TokenKind.Tilde => "__invert__",
    _ => null,
};
```

**Step 2 — Insert user-type check in BinaryExpr** (at line 1638, AFTER the dict merge `break;` and BEFORE `var isArithmetic`):

```csharp
// ── User-defined operator dispatch ──
var dunderName = BinaryOpToDunder(binOp);
if (dunderName is not null)
{
    // Check left operand for dunder
    var leftTypeName = leftType?.DisplayName;
    if (leftTypeName is not null && _typeDefs.TryGetValue(leftTypeName, out var leftTypeDef) &&
        leftTypeDef.Methods.Any(m => m.Name == dunderName))
    {
        LowerExpression(bin.Left);
        LowerExpression(bin.Right);
        _currentBlock!.Emit(new IrCallMethod(leftTypeName, dunderName, 1, expr.Span));
        break;
    }
    // __ne__ fallback: if type has __eq__ but not __ne__, emit __eq__ + not
    if (binOp == IrBinaryOpKind.NotEqual && leftTypeName is not null &&
        _typeDefs.TryGetValue(leftTypeName, out var neTypeDef) &&
        neTypeDef.Methods.Any(m => m.Name == "__eq__"))
    {
        LowerExpression(bin.Left);
        LowerExpression(bin.Right);
        _currentBlock!.Emit(new IrCallMethod(leftTypeName, "__eq__", 1, expr.Span));
        _currentBlock.Emit(new IrUnaryOp(IrUnaryOpKind.LogicalNot, expr.Span));
        break;
    }
}
```

**Step 3 — Insert user-type check in UnaryExpr** (replace lines 1685-1690):

Current code:
```csharp
case UnaryExpr unary:
    LowerExpression(unary.Operand);
    if (unary.Op == Lexer.TokenKind.KwNot)
        EmitTruthinessIfNeeded(unary.Operand);
    _currentBlock.Emit(new IrUnaryOp(MapUnaryOp(unary.Op), expr.Span));
    break;
```

Replace with:
```csharp
case UnaryExpr unary:
{
    var unaryDunder = UnaryOpToDunder(unary.Op);
    var operandType = _typeChecker.ResolvedTypes.TryGetValue(unary.Operand, out var uot) ? uot : null;
    var operandTypeName = operandType?.DisplayName;
    if (unaryDunder is not null && operandTypeName is not null &&
        _typeDefs.TryGetValue(operandTypeName, out var uTypeDef) &&
        uTypeDef.Methods.Any(m => m.Name == unaryDunder))
    {
        LowerExpression(unary.Operand);
        _currentBlock!.Emit(new IrCallMethod(operandTypeName, unaryDunder, 0, expr.Span));
        break;
    }
    LowerExpression(unary.Operand);
    if (unary.Op == Lexer.TokenKind.KwNot)
        EmitTruthinessIfNeeded(unary.Operand);
    _currentBlock!.Emit(new IrUnaryOp(MapUnaryOp(unary.Op), expr.Span));
    break;
}
```

### Tests

```csharp
[Fact]
public void OperatorSyntax_Plus_CallsAdd()
{
    var output = CompileAndRun("""
        class Vec:
            x: int = 0
            y: int = 0
            def __init__(x: int, y: int):
                @x = x
                @y = y
            def __add__(other: Vec) -> Vec:
                return Vec(int(@x) + int(other.x), int(@y) + int(other.y))
            def __str__() -> str:
                return f"({@x}, {@y})"
        def main():
            a = Vec(1, 2)
            b = Vec(3, 4)
            c = a + b
            print(c)
        """);
    Assert.Equal("(4, 6)", output);
}

[Fact]
public void OperatorSyntax_Eq_CallsEq()
{
    var output = CompileAndRun("""
        class Point:
            x: int = 0
            y: int = 0
            def __init__(x: int, y: int):
                @x = x
                @y = y
            def __eq__(other: Point) -> bool:
                return int(@x) == int(other.x) and int(@y) == int(other.y)
        def main():
            a = Point(1, 2)
            b = Point(1, 2)
            c = Point(3, 4)
            print(a == b)
            print(a == c)
        """);
    Assert.Equal("True\nFalse", output);
}

[Fact]
public void OperatorSyntax_Neg_CallsNeg()
{
    var output = CompileAndRun("""
        class Num:
            val: int = 0
            def __init__(v: int):
                @val = v
            def __neg__() -> Num:
                return Num(0 - int(@val))
            def __str__() -> str:
                return str(@val)
        def main():
            n = Num(5)
            print(-n)
        """);
    Assert.Equal("-5", output);
}

[Fact]
public void OperatorSyntax_Primitives_StillWork()
{
    // Verify primitive ops aren't broken by the new check
    var output = CompileAndRun("""
        def main():
            print(1 + 2)
            print(10 - 3)
            print(4 * 5)
            print(3 == 3)
        """);
    Assert.Equal("3\n7\n20\nTrue", output);
}
```

### Why It Works

- `leftType?.DisplayName` returns `null` for `PrimitiveType` (int, float, str) — the check falls through to the existing primitive path.
- For user types (`ClassType`), `DisplayName` returns the class name (e.g., `"Vec"`), and `_typeDefs` contains all user-defined type IR definitions.
- `IrCallMethod("Vec", "__add__", 1, ...)` calls the instance method. The emitter already resolves this via `_methodBuilders["Vec.__add__"]` which was registered when the dunder method was emitted.

---

## GAP-2: Class Decorators Silently Dropped

### The Bug

`Parser.cs:516-520` — decorators parsed before a class are never passed to the ClassDef:

```csharp
if (Current.Kind == TokenKind.KwClass)
{
    var cls = ParseClassDef();
    // Attach decorators to class (extend ClassDef if needed)
    return cls;   // ← decorators variable is never used!
}
```

Compare to functions (line 510): `return ParseFunctionDef(isAsync: false, decorators);` — decorators ARE passed.

### Current AST

**`Ast.cs:198-203`** — `ClassDef` has no `Decorators` field:
```csharp
public sealed record ClassDef(
    string Name,
    List<TypeParameter>? TypeParameters,
    List<TypeAnnotation> Bases,
    List<AstNode> Members,
    SourceSpan Span) : Statement(Span);
```

Compare to `FunctionDef` at line 176-183 which DOES have `List<Decorator> Decorators`.

### Exact Fix

**File: `src/Culebral.Compiler/Parser/Ast.cs`** — Add Decorators to ClassDef (line 198):

```csharp
public sealed record ClassDef(
    string Name,
    List<TypeParameter>? TypeParameters,
    List<TypeAnnotation> Bases,
    List<AstNode> Members,
    List<Decorator> Decorators,     // ← ADD
    SourceSpan Span) : Statement(Span);
```

**File: `src/Culebral.Compiler/Parser/Parser.cs`** — Three changes:

1. **`ParseClassDef` signature** (find `private ClassDef ParseClassDef()`):
   Add parameter: `private ClassDef ParseClassDef(List<Decorator>? decorators = null)`

2. **`ParseClassDef` return** — pass decorators to constructor:
   Change: `return new ClassDef(name, typeParams, bases, members, ...)`
   To: `return new ClassDef(name, typeParams, bases, members, decorators ?? [], ...)`

3. **`ParseDecoratedDef` line 516-520** — pass decorators:
   Change: `var cls = ParseClassDef(); return cls;`
   To: `return ParseClassDef(decorators);`

**File: `src/Culebral.Compiler/IR/Lowering.cs`** — In whatever method lowers ClassDef (search for `case ClassDef`), after creating the `IrTypeDef`, add:
```csharp
if (classDef.Decorators.Count > 0)
    typeDef.Decorators = ExtractDecorators(classDef.Decorators);
```
NOTE: `IrTypeDef` may need a `Decorators` property added (`List<IrDecorator>? Decorators`).

**File: `src/Culebral.Compiler/Emit/CilEmitter.cs`** — In type definition emission (where TypeBuilder is created), after defining the type:
```csharp
if (typeDef.Decorators is not null)
    ApplyDecoratorAttributes(tb, typeDef.Decorators);
```
NOTE: `ApplyDecoratorAttributes` currently takes a `MethodBuilder`. Need an overload or generic version for `TypeBuilder`.

### Compilation Ripple

Every place that constructs a `ClassDef` directly (not via `ParseClassDef`) needs the new `Decorators` param. Search for `new ClassDef(` — each callsite needs `[]` as the decorators argument.

### Tests

```csharp
[Fact]
public void ClassDecorator_Obsolete_EmitsAttribute()
{
    var output = CompileAndRun("""
        @Obsolete
        class OldService:
            def greet() -> str:
                return "hello"
        def main():
            s = OldService()
            print(s.greet())
        """);
    Assert.Equal("hello", output);
}
```

---

## GAP-3: F-String Format Specifications

### The Bug

`f"{3.14159:.2f}"` fails because `ParseFStringParts` at `Parser.cs:1726-1779` extracts the entire content between `{` and `}` as the expression. It feeds `3.14159:.2f` to the expression parser, which chokes on the `:`.

### Current Code

**`Parser.cs:1758`** — extracts everything between braces:
```csharp
var exprText = raw[exprStart..i];
// exprText is "3.14159:.2f" — the whole thing, colon and all
var exprLexer = new CulebralLexer(exprText, "<fstring>", _diagnostics);
```

**`Ast.cs:306`** — `FStringInterpolation` has no format spec field:
```csharp
public sealed record FStringInterpolation(Expression Expr, SourceSpan Span) : FStringPart(Span);
```

### Exact Fix

**File: `src/Culebral.Compiler/Parser/Ast.cs`** — Add FormatSpec (line 306):

```csharp
public sealed record FStringInterpolation(
    Expression Expr,
    string? FormatSpec,    // ← ADD: null means no format spec
    SourceSpan Span) : FStringPart(Span);
```

**File: `src/Culebral.Compiler/Parser/Parser.cs`** — In `ParseFStringParts`, after extracting `exprText` (line 1758), split on `:`:

```csharp
var exprText = raw[exprStart..i];
string? formatSpec = null;

// Split on ':' for format spec, but respect nested braces/brackets/parens
int depth = 0;
for (int ci = 0; ci < exprText.Length; ci++)
{
    char c = exprText[ci];
    if (c is '(' or '[' or '{') depth++;
    else if (c is ')' or ']' or '}') depth--;
    else if (c == ':' && depth == 0)
    {
        formatSpec = exprText[(ci + 1)..];
        exprText = exprText[..ci];
        break;
    }
}

if (i < raw.Length) i++; // skip '}'
textStart = i;

var exprLexer = new CulebralLexer(exprText, "<fstring>", _diagnostics);
var exprTokens = exprLexer.Tokenize();
var exprParser = new CulebralParser(exprTokens, _diagnostics);
var expr = exprParser.ParseExpression();
parts.Add(new FStringInterpolation(expr, formatSpec, span));
```

All other places that construct `FStringInterpolation` need the new `null` argument. Search for `new FStringInterpolation(` — likely just this one place.

**File: `src/Culebral.Compiler/IR/Lowering.cs`** — In `LowerFString`, the `FStringInterpolation` case (around line 2408):

Current:
```csharp
case FStringInterpolation interp:
    LowerExpression(interp.Expr);
    var exprType = ...;
    _currentBlock.Emit(new IrToString(exprType, fstr.Span));
    break;
```

Change: if `interp.FormatSpec` is not null, emit a format-based string conversion instead. Since `IrToString` currently takes only a `CulebralType`, the simplest approach is to emit `String.Format` directly:

```csharp
case FStringInterpolation interp:
    LowerExpression(interp.Expr);
    if (interp.FormatSpec is not null)
    {
        // Box value if needed, then String.Format("{0:spec}", value)
        var exprType2 = _typeChecker.ResolvedTypes.TryGetValue(interp.Expr, out var et2)
            ? et2 : PrimitiveType.Object;
        if (exprType2 is PrimitiveType pt && pt.ClrType is not null && pt.ClrType.IsValueType)
            _currentBlock!.Emit(new IrBox(pt, fstr.Span));
        _currentBlock!.Emit(new IrLoadString($"{{0:{interp.FormatSpec}}}", fstr.Span));
        // We need a new IR instruction or use IrCallDotNetStatic for String.Format
        // Simplest: emit as two-part string concat using String.Format
        _currentBlock.Emit(new IrCallDotNetStatic(typeof(string), "Format", 2, fstr.Span));
    }
    else
    {
        var exprType = _typeChecker.ResolvedTypes.TryGetValue(interp.Expr, out var et)
            ? et : PrimitiveType.Object;
        _currentBlock!.Emit(new IrToString(exprType, fstr.Span));
    }
    break;
```

Wait — the stack order is wrong above. `String.Format(string, object)` expects format string first, then value. But we pushed value first, then format string. Need to swap:

```csharp
if (interp.FormatSpec is not null)
{
    var exprType2 = _typeChecker.ResolvedTypes.TryGetValue(interp.Expr, out var et2)
        ? et2 : PrimitiveType.Object;
    if (exprType2 is PrimitiveType pt && pt.ClrType is not null && pt.ClrType.IsValueType)
        _currentBlock!.Emit(new IrBox(pt, fstr.Span));
    // Store value, load format, load value back
    var fmtTmp = CreateLocal("<fmt_val>", PrimitiveType.Object);
    _currentBlock!.Emit(new IrStoreLocal(fmtTmp.Index, fstr.Span));
    _currentBlock.Emit(new IrLoadString($"{{0:{interp.FormatSpec}}}", fstr.Span));
    _currentBlock.Emit(new IrLoadLocal(fmtTmp.Index, fstr.Span));
    _currentBlock.Emit(new IrCallDotNetStatic(typeof(string), "Format", 2, fstr.Span));
}
```

### Python→.NET Format Spec Behavior

We pass the format spec through to `String.Format` as-is. This means:
- `.2f` → `{0:.2f}` → .NET interprets as 2 decimal places fixed-point. **Works identically to Python.**
- `08d` → `{0:08d}` → .NET interprets as zero-padded 8-digit decimal. **Works.**
- `,` → `{0:,}` → .NET doesn't support bare `,` as thousand separator. **Diverges.** Document.
- `>10` → `{0:>10}` → .NET doesn't support `>` for right-align. **Diverges.** Document.

The overlap is large enough that passing through is the right pragmatic choice. Edge cases are documented as .NET-first behavior.

### Tests

```csharp
[Fact]
public void FString_FormatSpec_TwoDecimals()
{
    var output = CompileAndRun("""
        def main():
            pi = 3.14159
            print(f"{pi:.2f}")
        """);
    Assert.Equal("3.14", output);
}

[Fact]
public void FString_FormatSpec_ZeroPad()
{
    var output = CompileAndRun("""
        def main():
            n = 42
            print(f"{n:D8}")
        """);
    Assert.Equal("00000042", output);
}

[Fact]
public void FString_NoSpec_Unchanged()
{
    var output = CompileAndRun("""
        def main():
            x = 42
            print(f"value={x}")
        """);
    Assert.Equal("value=42", output);
}
```

---

## GAP-4: Nested Comprehensions

### The Bug

`[x for row in matrix for x in row]` fails to parse. The parser consumes one `for` clause and expects `]`.

### Current Code

**`Ast.cs:398-403`** — single for clause:
```csharp
public sealed record ListComprehension(
    Expression Element,
    string Variable,           // single variable
    Expression Iterable,       // single iterable
    Expression? Condition,     // single condition
    SourceSpan Span) : Expression(Span);
```

**`Parser.cs:1610-1622`** — parses exactly one `for`:
```csharp
if (Current.Kind == TokenKind.KwFor)
{
    Advance();
    var variable = Expect(TokenKind.Identifier).Lexeme;
    Expect(TokenKind.KwIn);
    var iterable = ParseOr();
    Expression? condition = null;
    if (TryConsume(TokenKind.KwIf))
        condition = ParseOr();
    Expect(TokenKind.RightBracket);  // ← immediately expects ] after one clause
    return new ListComprehension(first, variable, iterable, condition, ...);
}
```

**`Lowering.cs:2447-2507`** — generates one loop:
```csharp
LowerExpression(comp.Iterable);            // single iterable
// ... single GetEnumerator/MoveNext loop ...
var varLocal = GetOrCreateLocal(comp.Variable, comp.Span);  // single variable
```

### Exact Fix

**File: `src/Culebral.Compiler/Parser/Ast.cs`** — Add `ComprehensionClause`, modify comprehension records:

```csharp
public sealed record ComprehensionClause(
    string Variable,
    Expression Iterable,
    Expression? Condition,
    SourceSpan Span) : AstNode(Span);

public sealed record ListComprehension(
    Expression Element,
    List<ComprehensionClause> Clauses,    // ← replaces Variable/Iterable/Condition
    SourceSpan Span) : Expression(Span);
```

Apply the same change to `DictComprehension`, `SetComprehension`, `GeneratorExpr`.

**File: `src/Culebral.Compiler/Parser/Parser.cs`** — Change list comprehension parsing (line 1610):

```csharp
if (Current.Kind == TokenKind.KwFor)
{
    var clauses = new List<ComprehensionClause>();
    while (Current.Kind == TokenKind.KwFor)
    {
        var clauseStart = Current.Span.Start;
        Advance(); // consume 'for'
        var variable = Expect(TokenKind.Identifier).Lexeme;
        Expect(TokenKind.KwIn);
        var iterable = ParseOr();
        Expression? condition = null;
        if (Current.Kind == TokenKind.KwIf && Peek(1).Kind != TokenKind.KwFor)
            // Only consume 'if' if it's a filter, not if next token is 'for'
            // Actually: just consume it — the next iteration of while checks for 'for'
        if (TryConsume(TokenKind.KwIf))
            condition = ParseOr();
        clauses.Add(new ComprehensionClause(variable, iterable, condition,
            new SourceSpan(clauseStart, CurrentLocation())));
    }
    Expect(TokenKind.RightBracket);
    return new ListComprehension(first, clauses, new SourceSpan(start, CurrentLocation()));
}
```

**File: `src/Culebral.Compiler/Semantics/TypeChecker.cs`** — `InferListComprehension` needs to create nested scopes for each clause's variable.

**File: `src/Culebral.Compiler/IR/Lowering.cs`** — `LowerListComprehension` generates nested loops:

```csharp
private void LowerListComprehension(ListComprehension comp)
{
    // Create result list
    var listLocal = CreateLocal("<comp_list>", PrimitiveType.Object);
    _currentBlock!.Emit(new IrNewObj("System.Collections.Generic.List`1", 0, comp.Span));
    _currentBlock.Emit(new IrStoreLocal(listLocal.Index, comp.Span));

    // Generate nested loops — one per clause
    var endLabels = new Stack<string>();
    var condLabels = new Stack<string>();

    foreach (var clause in comp.Clauses)
    {
        LowerExpression(clause.Iterable);
        var enumLocal = CreateLocal($"<comp_enum_{clause.Variable}>", PrimitiveType.Object);
        _currentBlock!.Emit(new IrCallVirtual("GetEnumerator", 0, comp.Span));
        _currentBlock.Emit(new IrStoreLocal(enumLocal.Index, comp.Span));

        var condLabel = NewBlockLabel($"comp_cond_{clause.Variable}");
        var bodyLabel = NewBlockLabel($"comp_body_{clause.Variable}");
        var endLabel = NewBlockLabel($"comp_end_{clause.Variable}");
        condLabels.Push(condLabel);
        endLabels.Push(endLabel);

        _currentBlock.Emit(new IrBranch(condLabel, comp.Span));

        // Condition block: MoveNext
        var condBlock = new IrBasicBlock { Label = condLabel };
        _currentFunction!.Body.Add(condBlock);
        _currentBlock = condBlock;
        _currentBlock.Emit(new IrLoadLocal(enumLocal.Index, comp.Span));
        _currentBlock.Emit(new IrCallVirtual("MoveNext", 0, comp.Span));
        _currentBlock.Emit(new IrBranchIf(bodyLabel, endLabel, comp.Span));

        // Body block: store Current to variable
        var bodyBlock = new IrBasicBlock { Label = bodyLabel };
        _currentFunction.Body.Add(bodyBlock);
        _currentBlock = bodyBlock;
        var varLocal = GetOrCreateLocal(clause.Variable, comp.Span);
        _currentBlock.Emit(new IrLoadLocal(enumLocal.Index, comp.Span));
        _currentBlock.Emit(new IrCallVirtual("get_Current", 0, comp.Span));
        _currentBlock.Emit(new IrStoreLocal(varLocal.Index, comp.Span));

        // Optional condition filter
        if (clause.Condition is not null)
        {
            var addLabel = NewBlockLabel($"comp_add_{clause.Variable}");
            LowerExpression(clause.Condition);
            _currentBlock.Emit(new IrBranchIf(addLabel, condLabel, comp.Span));
            var addBlock = new IrBasicBlock { Label = addLabel };
            _currentFunction.Body.Add(addBlock);
            _currentBlock = addBlock;
        }
    }

    // Innermost body: add element to list
    _currentBlock!.Emit(new IrLoadLocal(listLocal.Index, comp.Span));
    LowerExpression(comp.Element);
    _currentBlock.Emit(new IrCallVirtual("Add", 1, comp.Span));

    // Branch back to innermost condition
    _currentBlock.Emit(new IrBranch(condLabels.Peek(), comp.Span));

    // Close loops in reverse order
    while (endLabels.Count > 0)
    {
        var endLabel = endLabels.Pop();
        var condLabel = condLabels.Pop();
        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction!.Body.Add(endBlock);
        _currentBlock = endBlock;

        // If there's an outer loop, branch back to its condition
        if (condLabels.Count > 0)
            _currentBlock.Emit(new IrBranch(condLabels.Peek(), comp.Span));
    }

    _currentBlock!.Emit(new IrLoadLocal(listLocal.Index, comp.Span));
}
```

### Tests

```csharp
[Fact]
public void NestedComprehension_FlattenMatrix()
{
    var output = CompileAndRun("""
        def main():
            matrix = [[1, 2], [3, 4], [5, 6]]
            flat = [x for row in matrix for x in row]
            print(len(flat))
        """);
    Assert.Equal("6", output);
}
```

---

## GAP-5: Starred Unpacking in Assignments

### The Bug

`a, *rest = [1, 2, 3, 4]` doesn't parse. Only fixed-count tuple unpacking works.

### Current Code

**`Ast.cs:367-369`** — `TupleExpr` has no starred marker:
```csharp
public sealed record TupleExpr(
    List<Expression> Elements,
    SourceSpan Span) : Expression(Span);
```

Assignment target parsing in the lowering only handles `TupleExpr` with a fixed number of elements matched 1:1 against the RHS.

### Exact Fix

**File: `src/Culebral.Compiler/Parser/Ast.cs`** — Add `StarredExpr`:
```csharp
public sealed record StarredExpr(
    Expression Operand,
    SourceSpan Span) : Expression(Span);
```

**File: `src/Culebral.Compiler/Parser/Parser.cs`** — In expression parsing, when parsing tuple elements (assignment targets), check for `*`:
```csharp
// When parsing assignment target and current token is Star:
if (Current.Kind == TokenKind.Star)
{
    Advance();
    var operand = ParsePrimary(); // the identifier after *
    elements.Add(new StarredExpr(operand, ...));
}
```

**File: `src/Culebral.Compiler/IR/Lowering.cs`** — In tuple unpacking logic, detect `StarredExpr`:

```
Given: a, *rest, b = items

head_count = count of elements BEFORE the StarredExpr (1: a)
tail_count = count of elements AFTER the StarredExpr (1: b)
starred_index = index of the StarredExpr (1: rest)

Emit:
  a = items[0]                                    // head elements
  b = items[items.Count - 1]                     // tail elements (from end)
  rest = items.GetRange(1, items.Count - 2)      // middle slice
```

### Tests

```csharp
[Fact]
public void StarredUnpacking_RestAtEnd()
{
    var output = CompileAndRun("""
        def main():
            a, *rest = [1, 2, 3, 4]
            print(a)
            print(len(rest))
        """);
    Assert.Equal("1\n3", output);
}
```

---

## GAP-6: `str.join()`

### The Bug

`", ".join(["a", "b", "c"])` fails because `.join` on a string resolves to nothing useful. Python's `str.join(iterable)` maps to .NET's `string.Join(separator, iterable)` — but the receiver and argument are swapped.

### Current Code

**`CilEmitter.cs:3177-3211`** — `EmitVirtualCall` has a switch for known methods, then falls through to alias lookup + `FindDotNetMethod`. `join` doesn't match anything.

### Exact Fix

**File: `src/Culebral.Compiler/Emit/CilEmitter.cs`** — In `EmitVirtualCall`, add to the switch (after `case "Add" when argc == 1:` block, before the alias lookup):

```csharp
case "join" when argc == 1:
{
    // Python: separator.join(iterable) → string.Join(separator, iterable)
    // Stack: [separator (string), iterable (object)]
    var joinIter = il.DeclareLocal(typeof(object));
    il.Emit(OpCodes.Stloc, joinIter);       // save iterable
    // separator is now on top of stack (string)
    il.Emit(OpCodes.Ldloc, joinIter);        // push iterable
    il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
    il.Emit(OpCodes.Call, typeof(string).GetMethod("Join",
        [typeof(string), typeof(System.Collections.IEnumerable)])!);
    return;
}
```

### Tests

```csharp
[Fact]
public void StringJoin_CommaSeparated()
{
    var output = CompileAndRun("""
        def main():
            items = ["a", "b", "c"]
            print(", ".join(items))
        """);
    Assert.Equal("a, b, c", output);
}
```

---

## GAP-7: `dict.get(key, default)`

### The Bug

`d.get("key", "fallback")` resolves to nothing. `Dictionary<object,object>` has no `Get` method.

### Exact Fix

**File: `src/Culebral.Compiler/Emit/CilEmitter.cs`** — In `EmitVirtualCall` switch:

```csharp
case "get" when argc == 1:
{
    // dict.get(key) → TryGetValue(key, out v) ? v : null
    var getKey = il.DeclareLocal(typeof(object));
    il.Emit(OpCodes.Stloc, getKey);
    il.Emit(OpCodes.Ldloc, getKey);
    var getOut = il.DeclareLocal(typeof(object));
    il.Emit(OpCodes.Ldloca, getOut);
    il.Emit(OpCodes.Callvirt, typeof(Dictionary<object, object>)
        .GetMethod("TryGetValue")!);
    var getFound = il.DefineLabel();
    var getEnd = il.DefineLabel();
    il.Emit(OpCodes.Brtrue, getFound);
    il.Emit(OpCodes.Ldnull);
    il.Emit(OpCodes.Br, getEnd);
    il.MarkLabel(getFound);
    il.Emit(OpCodes.Ldloc, getOut);
    il.MarkLabel(getEnd);
    return;
}
case "get" when argc == 2:
{
    // dict.get(key, default) → TryGetValue(key, out v) ? v : default
    var getDefault = il.DeclareLocal(typeof(object));
    il.Emit(OpCodes.Stloc, getDefault);
    var getKey2 = il.DeclareLocal(typeof(object));
    il.Emit(OpCodes.Stloc, getKey2);
    il.Emit(OpCodes.Ldloc, getKey2);
    var getOut2 = il.DeclareLocal(typeof(object));
    il.Emit(OpCodes.Ldloca, getOut2);
    il.Emit(OpCodes.Callvirt, typeof(Dictionary<object, object>)
        .GetMethod("TryGetValue")!);
    var getFound2 = il.DefineLabel();
    var getEnd2 = il.DefineLabel();
    il.Emit(OpCodes.Brtrue, getFound2);
    il.Emit(OpCodes.Ldloc, getDefault);
    il.Emit(OpCodes.Br, getEnd2);
    il.MarkLabel(getFound2);
    il.Emit(OpCodes.Ldloc, getOut2);
    il.MarkLabel(getEnd2);
    return;
}
```

### Tests

```csharp
[Fact]
public void DictGet_KeyExists_ReturnsValue()
{
    var output = CompileAndRun("""
        def main():
            d = {"a": 1, "b": 2}
            print(d.get("a", 0))
        """);
    Assert.Equal("1", output);
}

[Fact]
public void DictGet_KeyMissing_ReturnsDefault()
{
    var output = CompileAndRun("""
        def main():
            d = {"a": 1}
            print(d.get("z", 99))
        """);
    Assert.Equal("99", output);
}
```

---

## GAP-8: OR Patterns in Match

### The Bug

`case 1 | 2 | 3:` fails to parse. C# supports `case 1 or 2 or 3:`.

### Current Code

**`Parser.cs:754-758`** — `ParseMatchCase` calls `ParsePattern()` which returns a single pattern:
```csharp
Expect(TokenKind.KwCase);
var pattern = ParsePattern();
```

**`Ast.cs:137-155`** — No `OrPattern` exists:
```csharp
public abstract record Pattern(SourceSpan Span) : AstNode(Span);
public sealed record WildcardPattern(...);
public sealed record NamePattern(...);
public sealed record LiteralPattern(...);
public sealed record TypePattern(...);
public sealed record ConstructorPattern(...);
public sealed record NonePattern(...);
```

### Exact Fix

**File: `src/Culebral.Compiler/Parser/Ast.cs`** — Add after `NonePattern` (line 155):
```csharp
public sealed record OrPattern(
    List<Pattern> Alternatives,
    SourceSpan Span) : Pattern(Span);
```

**File: `src/Culebral.Compiler/Parser/Parser.cs`** — Change `ParseMatchCase` (line 758):
```csharp
// Before:
var pattern = ParsePattern();
// After:
var pattern = ParsePattern();
if (Current.Kind == TokenKind.Pipe)
{
    var alternatives = new List<Pattern> { pattern };
    while (TryConsume(TokenKind.Pipe))
        alternatives.Add(ParsePattern());
    pattern = new OrPattern(alternatives, new SourceSpan(start, CurrentLocation()));
}
```

**File: `src/Culebral.Compiler/IR/Lowering.cs`** — In match case lowering (search for pattern matching logic), add `OrPattern` case:
```csharp
case OrPattern or:
{
    // Try each alternative: if any matches, branch to the case body
    var nextAlt = NewBlockLabel("or_next");
    foreach (var alt in or.Alternatives)
    {
        // Emit pattern check for this alternative
        EmitPatternCheck(alt, subjectLocal, caseBodyLabel);
        // If it didn't branch to body, try next
    }
    // None matched — fall through to next case
    _currentBlock.Emit(new IrBranch(nextCaseLabel, matchCase.Span));
    break;
}
```

The exact implementation depends on how patterns currently emit their checks. The key idea: for each alternative, emit the same check logic that a single pattern would use, but branch to the shared body label on success.

### Tests

```csharp
[Fact]
public void MatchOrPattern_MatchesAlternative()
{
    var output = CompileAndRun("""
        def main():
            x = 2
            match x:
                case 1 | 2 | 3:
                    print("small")
                case _:
                    print("other")
        """);
    Assert.Equal("small", output);
}
```

---

## GAP-9: `raise ... from` (Exception Chaining)

### The Bug

`raise ValueError("bad") from original` doesn't parse.

### Current Code

**`Parser.cs:695-702`**:
```csharp
private RaiseStatement ParseRaiseStatement()
{
    Advance(); // consume 'raise'
    Expression? value = null;
    if (Current.Kind != TokenKind.Newline && Current.Kind != TokenKind.EndOfFile)
        value = ParseExpression();
    return new RaiseStatement(value, new SourceSpan(start, CurrentLocation()));
}
```

**`Ast.cs:37-39`**:
```csharp
public sealed record RaiseStatement(
    Expression? Value,
    SourceSpan Span) : Statement(Span);
```

**`Lowering.cs:983-987`**:
```csharp
case RaiseStatement raise:
    if (raise.Value is not null)
        LowerExpression(raise.Value);
    _currentBlock.Emit(new IrThrow(raise.Span));
    break;
```

### Exact Fix

**File: `src/Culebral.Compiler/Parser/Ast.cs`** — Add `Cause` (line 37):
```csharp
public sealed record RaiseStatement(
    Expression? Value,
    Expression? Cause,      // ← ADD
    SourceSpan Span) : Statement(Span);
```

**File: `src/Culebral.Compiler/Parser/Parser.cs`** — Parse `from` (line 700):
```csharp
private RaiseStatement ParseRaiseStatement()
{
    var start = Current.Span.Start;
    Advance();
    Expression? value = null;
    Expression? cause = null;
    if (Current.Kind != TokenKind.Newline && Current.Kind != TokenKind.EndOfFile)
    {
        value = ParseExpression();
        if (TryConsume(TokenKind.KwFrom))
            cause = ParseExpression();
    }
    return new RaiseStatement(value, cause, new SourceSpan(start, CurrentLocation()));
}
```

NOTE: `KwFrom` must exist as a token kind. Check if `from` is already a keyword — it is (used in `from X import Y`).

**File: `src/Culebral.Compiler/IR/Lowering.cs`** — When cause is present:
```csharp
case RaiseStatement raise:
    if (raise.Value is not null)
    {
        if (raise.Cause is not null)
        {
            // raise Exception("msg") from cause
            // → new Exception("msg", cause) with InnerException
            // The value is likely a constructor call. We need to inject the cause
            // as a second constructor argument.
            // Simplest: lower the value, lower the cause, set InnerException property
            LowerExpression(raise.Value);
            // Store the exception, set its InnerException field via reflection or
            // re-construct with 2-arg constructor
            // Pragmatic: just lower both and emit a warning for now
            LowerExpression(raise.Cause);
            _currentBlock!.Emit(new IrPop(raise.Span)); // drop cause for now
        }
        else
        {
            LowerExpression(raise.Value);
        }
    }
    _currentBlock!.Emit(new IrThrow(raise.Span));
    break;
```

Full implementation: After lowering the exception constructor call, store to local, load local, load cause, call a helper that sets `InnerException` via reflection (it has a private setter). Or better: detect that the raised expression is a constructor call and inject the cause as a second argument to `Exception(string, Exception)`.

### Tests

```csharp
[Fact]
public void RaiseFrom_Parses()
{
    // Just verify it compiles — full InnerException propagation is a refinement
    var output = CompileAndRun("""
        def main():
            try:
                raise Exception("outer") from Exception("inner")
            except Exception as e:
                print(e.message)
        """);
    Assert.Equal("outer", output);
}
```

---

## GAP-10: Missing String Methods

### The Bug

`s.count("x")`, `s.isdigit()`, `s.isalpha()`, `s.isspace()`, `s.index("x")` don't resolve.

### Exact Fix

**File: `src/Culebral.Compiler/Emit/CilEmitter.cs`** — Add to `PythonStringAliases`:
```csharp
["index"] = "IndexOf",
```

Add special cases in `EmitVirtualCall` for methods without direct .NET equivalents:

```csharp
case "isdigit" when argc == 0:
case "isalpha" when argc == 0:
case "isspace" when argc == 0:
{
    // s.isdigit() → s.Length > 0 && s.All(char.IsDigit)
    // Emit inline: iterate chars, check each, short-circuit on false
    var charCheckMethod = name switch
    {
        "isdigit" => typeof(char).GetMethod("IsDigit", [typeof(char)])!,
        "isalpha" => typeof(char).GetMethod("IsLetter", [typeof(char)])!,
        "isspace" => typeof(char).GetMethod("IsWhiteSpace", [typeof(char)])!,
        _ => throw new InvalidOperationException()
    };
    EmitStringCharCheck(il, charCheckMethod);
    return;
}

case "count" when argc == 1:
{
    // s.count(sub) → count occurrences
    // (s.Length - s.Replace(sub, "").Length) / sub.Length
    var countSub = il.DeclareLocal(typeof(string));
    il.Emit(OpCodes.Stloc, countSub);
    var countStr = il.DeclareLocal(typeof(string));
    il.Emit(OpCodes.Stloc, countStr);
    // str.Length
    il.Emit(OpCodes.Ldloc, countStr);
    il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length")!.GetGetMethod()!);
    // str.Replace(sub, "").Length
    il.Emit(OpCodes.Ldloc, countStr);
    il.Emit(OpCodes.Ldloc, countSub);
    il.Emit(OpCodes.Ldstr, "");
    il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("Replace", [typeof(string), typeof(string)])!);
    il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length")!.GetGetMethod()!);
    il.Emit(OpCodes.Sub);
    // / sub.Length
    il.Emit(OpCodes.Ldloc, countSub);
    il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length")!.GetGetMethod()!);
    il.Emit(OpCodes.Div);
    return;
}
```

Add helper:
```csharp
private void EmitStringCharCheck(ILGenerator il, MethodInfo charCheck)
{
    // Stack: string. Returns bool (true if all chars pass check AND length > 0)
    var str = il.DeclareLocal(typeof(string));
    il.Emit(OpCodes.Stloc, str);
    // Check length > 0
    il.Emit(OpCodes.Ldloc, str);
    il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length")!.GetGetMethod()!);
    var empty = il.DefineLabel();
    il.Emit(OpCodes.Brfalse, empty);
    // Loop through chars
    var idx = il.DeclareLocal(typeof(int));
    il.Emit(OpCodes.Ldc_I4_0);
    il.Emit(OpCodes.Stloc, idx);
    var loopCheck = il.DefineLabel();
    var loopBody = il.DefineLabel();
    il.Emit(OpCodes.Br, loopCheck);
    il.MarkLabel(loopBody);
    il.Emit(OpCodes.Ldloc, str);
    il.Emit(OpCodes.Ldloc, idx);
    il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("get_Chars", [typeof(int)])!);
    il.Emit(OpCodes.Call, charCheck);
    var fail = il.DefineLabel();
    il.Emit(OpCodes.Brfalse, fail);
    il.Emit(OpCodes.Ldloc, idx);
    il.Emit(OpCodes.Ldc_I4_1);
    il.Emit(OpCodes.Add);
    il.Emit(OpCodes.Stloc, idx);
    il.MarkLabel(loopCheck);
    il.Emit(OpCodes.Ldloc, idx);
    il.Emit(OpCodes.Ldloc, str);
    il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length")!.GetGetMethod()!);
    il.Emit(OpCodes.Blt, loopBody);
    il.Emit(OpCodes.Ldc_I4_1); // all passed
    var end = il.DefineLabel();
    il.Emit(OpCodes.Br, end);
    il.MarkLabel(empty);
    il.MarkLabel(fail);
    il.Emit(OpCodes.Ldc_I4_0); // empty or failed
    il.MarkLabel(end);
}
```

### Tests

```csharp
[Fact]
public void String_Isdigit_True() {
    var output = CompileAndRun("""
        def main():
            print("12345".isdigit())
        """);
    Assert.Equal("True", output);
}

[Fact]
public void String_Count_Occurrences() {
    var output = CompileAndRun("""
        def main():
            print("banana".count("an"))
        """);
    Assert.Equal("2", output);
}
```

---

## GAP-11: Function Argument Count Validation

### The Bug

`def foo(a, b):` can be called as `foo(1)` or `foo(1,2,3)` without compile-time error. Only crashes at runtime.

### Current Code

**`TypeChecker.cs:954-962`**:
```csharp
private CulebralType InferCall(CallExpr call)
{
    var calleeType = InferType(call.Callee);
    foreach (var arg in call.Arguments)
        InferType(arg.Value);
    if (calleeType is FunctionType funcType)
        return funcType.ReturnType;
    // ... no argument count check anywhere
```

### Exact Fix

**File: `src/Culebral.Compiler/Semantics/TypeChecker.cs`** — After `foreach (var arg ...)` and before the return:

```csharp
// Validate argument count for known functions
if (call.Callee is IdentifierExpr callIdent)
{
    var sym = _currentScope.Lookup(callIdent.Name);
    if (sym?.Kind == SymbolKind.Function && sym is FunctionSymbol fs)
    {
        var required = fs.Parameters.Count(p => !p.HasDefault);
        var maxAllowed = fs.HasVarArgs ? int.MaxValue : fs.Parameters.Count;
        var actual = call.Arguments.Count;
        if (actual < required)
            _diagnostics.Error("LEB2020",
                $"'{callIdent.Name}' requires {required} argument(s), got {actual}",
                call.Span);
        else if (actual > maxAllowed)
            _diagnostics.Error("LEB2021",
                $"'{callIdent.Name}' accepts at most {fs.Parameters.Count} argument(s), got {actual}",
                call.Span);
    }
}
```

NOTE: This requires `FunctionSymbol` to have `Parameters` with `HasDefault` and `HasVarArgs` info. Check what information the symbol table stores about functions. If it only stores a `FunctionType` (return type + param types), the parameter metadata may need enrichment.

Also: SKIP validation for builtins with multiple overloads (`range`, `int`, `round`, `min`, `max`, `pow`) — these accept variable arg counts by design.

### Tests

```csharp
[Fact]
public void ArgCount_TooFew_ReportsError()
{
    // Should produce compilation error, not runtime crash
    var source = """
        def greet(name: str, greeting: str):
            print(f"{greeting}, {name}")
        def main():
            greet("Alice")
        """;
    // Compile and check diagnostics contain LEB2020
}
```

---

## Implementation Priority

| # | Gap | Effort | Files | Key Risk |
|---|---|---|---|---|
| 1 | Operator dispatch | Medium | Lowering.cs | Must not break primitive ops |
| 2 | Class decorators | Low | Ast.cs, Parser.cs, Lowering.cs, CilEmitter.cs | `ClassDef` record change ripples |
| 3 | F-string format specs | Medium | Ast.cs, Parser.cs, Lowering.cs | Colon parsing edge cases |
| 4 | str.join() | Low | CilEmitter.cs only | Stack order |
| 5 | dict.get() | Low | CilEmitter.cs only | TryGetValue out-param CIL |
| 6 | OR patterns in match | Medium | Ast.cs, Parser.cs, Lowering.cs | Pattern lowering integration |
| 7 | Nested comprehensions | High | Ast.cs, Parser.cs, TypeChecker.cs, Lowering.cs | Breaking AST change |
| 8 | raise ... from | Low | Ast.cs, Parser.cs, Lowering.cs | InnerException injection |
| 9 | Starred unpacking | Medium | Ast.cs, Parser.cs, Lowering.cs | Index arithmetic |
| 10 | Arg count validation | Medium | TypeChecker.cs | Symbol table enrichment |
| 11 | String methods | Low | CilEmitter.cs | CIL char-loop emission |
