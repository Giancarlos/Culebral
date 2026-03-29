# Culebral — Known Issues & Technical Debt

> Honest assessment of weaknesses in the codebase. Ordered by risk to production use.
> Each issue includes reproduction steps, root cause, and fix approach.

---

## ISSUE-1: `EmitVirtualCall` Is a Fragile Special-Case Tower

**Severity:** HIGH — silent wrong behavior on type mismatch
**File:** `src/Culebral.Compiler/Emit/CilEmitter.cs`, `EmitVirtualCall` method (~line 3606)

### Problem

Every Python collection/string method (`append`, `pop`, `keys`, `values`, `items`, `join`, `get`, `isdigit`, `isalpha`, `isspace`, `count`, `clear`, `copy`, `insert`) is a hand-coded `if (name == "xxx" && argc == N)` block with hand-written CIL. The method is ~400 lines of sequential conditionals with no type checking on the receiver.

### What breaks

```python
"hello".append(1)     # string has no append — should error, but emits IList.Add CIL → InvalidProgramException
42.keys()             # int has no keys — hits MissingMethodException (good), but the error message says "Method 'keys' not found" with no context about what type was expected
[1,2,3].get("a", 0)  # list has no get — executes dict.get CIL on a list → InvalidCastException at runtime
```

None of these produce compile-time errors. All crash at runtime with .NET exception types that mean nothing to a Culebral developer.

### Root cause

`EmitVirtualCall` has no receiver type information. It dispatches purely on method name and argument count. The lowering emits `IrCallVirtual("append", 1)` without attaching the receiver's type. The emitter guesses based on the method name.

### Fix approach

1. Add a `ReceiverType` field to `IrCallVirtual` (or create a new instruction `IrCallCollectionMethod` that carries the inferred collection type).
2. In the lowering, when the type checker knows the receiver is a `list`, `dict`, `set`, or `str`, tag the call with that type.
3. In the emitter, dispatch based on `(receiverType, methodName)` pairs instead of just method name.
4. When the receiver type is unknown, emit a runtime type check with a meaningful error message.

### Tests needed

```csharp
[Fact] String_Append_ThrowsMeaningfulError()    // "hello".append(1) → compile error or clear runtime error
[Fact] Int_Keys_ThrowsMeaningfulError()         // 42.keys() → clear error
[Fact] List_Get_ThrowsMeaningfulError()         // [1,2].get("a") → clear error, not InvalidCastException
```

---

## ISSUE-2: Generator State Machine Not Tested for Complex Control Flow

**Severity:** HIGH — silent wrong results in generators
**File:** `src/Culebral.Compiler/Emit/CilEmitter.cs`, `EmitGeneratorStateMachine` (~line 2262)

### Problem

The generator state machine was tested with simple patterns:
- `while` loop with single `yield`
- `for` loop with conditional `yield`
- Sequential `yield` statements
- Infinite generator with `break`

Never tested:
- `yield` inside `try/except` — state suspension across exception handlers is extremely tricky in CIL. The CLR forbids `leave` from inside certain handler blocks.
- `yield` inside nested `for` loops — two loop levels with yield requires correct state tracking across both levels.
- Multiple `yield` points in different `if/else` branches — state numbers must correctly resume into the right branch.
- Generator iterated by two consumers simultaneously — `GetEnumerator()` returns `this`, so two `for` loops over the same generator share state.
- Generator with `return` statement before final yield — should stop iteration.

### What likely breaks

```python
def careful():
    try:
        yield 1
        yield 2
    except Exception:
        yield -1

# This likely crashes with InvalidProgramException because the CIL verifier
# rejects yield (ret) from inside an exception handler region.
```

```python
def matrix_gen():
    for row in range(3):
        for col in range(3):
            yield row * 10 + col

# Two nested loops with yield — the inner loop's MoveNext resumption
# must re-enter the inner loop body, not restart from the outer loop.
# State machine indices may be wrong.
```

### Root cause

The generator emitter uses a simple `yieldIndex` counter and creates resume labels linearly. It doesn't account for the control flow graph topology — it assumes yields are sequential in the IR instruction stream. Yields inside branches or loops break this assumption because the resume point may not be the next instruction after the yield.

### Fix approach

1. Write the failing tests FIRST to establish the actual behavior.
2. For yield-in-try: the CIL spec (ECMA-335 III.3.46) requires `leave` to exit protected regions before `ret`. The generator may need to store the yield value, leave the try block, then return.
3. For nested loops: verify that the resume labels land in the correct basic block by tracing the control flow.
4. For shared iterator: document as intentional limitation (Python generators are also single-use iterators). Or clone state in `GetEnumerator()`.

### Tests needed

```csharp
[Fact] Yield_InsideTryExcept()
[Fact] Yield_NestedForLoops()
[Fact] Yield_InIfElseBranches()
[Fact] Yield_TwoConsumers_IndependentIteration()
[Fact] Yield_EarlyReturn_StopsIteration()
```

---

## ISSUE-3: `with` Statement Has Pre-Existing Test Failures

**Severity:** HIGH — known broken feature
**Files:** `src/Culebral.Compiler/IR/Lowering.cs` (LowerWithStatement), `src/Culebral.Compiler/Emit/CilEmitter.cs` (try/finally emission)

### Problem

Two tests crash at runtime:
- `WithStatement_ResourceCleanup` — exit code 134 (InvalidProgramException)
- `AsyncWith_CompilesLikeRegularWith` — exit code 134

These have been failing since before the current work began. They were skipped/ignored but never investigated.

### Likely root cause

The `with` statement lowers to `try-finally` with `Dispose()` in the finally block. The CIL emission for `try-finally` may have stack balance issues — the `BeginExceptionBlock`/`BeginFinallyBlock`/`EndExceptionBlock` sequence in `System.Reflection.Emit` has strict ordering requirements. If the `Dispose()` call in the finally block leaves an unexpected value on the stack, the CIL verifier rejects the method.

### Fix approach

1. Run the failing test manually and capture the exact exception.
2. Emit the IR with `culebral ir test.leb` to see what instructions are generated.
3. Compare the CIL output with what a valid C# `using` statement produces.
4. Fix the stack balance in the finally block.

### Tests that fail

```csharp
WithStatement_ResourceCleanup   // exit 134
AsyncWith_CompilesLikeRegularWith  // exit 134
```

---

## ISSUE-4: F-String Format Specs Use .NET Syntax, Not Python Syntax

**Severity:** MEDIUM — wrong output, no error
**File:** `src/Culebral.Compiler/Parser/Parser.cs` (ParseFStringParts), `src/Culebral.Compiler/IR/Lowering.cs` (LowerFString)

### Problem

```python
pi = 3.14159
print(f"{pi:.2f}")   # Python expects "3.14"
                      # Culebral produces "32f" (wrong — .NET interprets .2f differently)
```

The format spec is passed through to `String.Format("{0:.2f}", value)`. But `.2f` is Python's format mini-language, not .NET's. The .NET equivalent is `F2`.

| Python spec | .NET spec | Meaning |
|---|---|---|
| `.2f` | `F2` | Fixed-point, 2 decimals |
| `08d` | `D8` | Zero-padded integer (partially works) |
| `>10` | no equivalent | Right-align (doesn't work) |
| `,` | `N0` | Thousands separator (doesn't work) |
| `#x` | `X` | Hex (different syntax) |

### Root cause

The parser correctly splits on `:` to extract the format spec (line ~1791). The lowering passes it verbatim to `String.Format`. No translation from Python format mini-language to .NET composite format strings.

### Fix approach

Option A: Translate Python format specs to .NET at compile time. Build a mapping table:
- `f` → `F` (fixed-point, strip leading `.`)
- `d` → `D` (decimal)
- `x` → `X` (hex)
- `>N` → right-align (emit `PadLeft`)
- `<N` → left-align (emit `PadRight`)
- `,` → `N` (number with grouping)

Option B: Document that format specs use .NET syntax (current state). Users write `f"{pi:F2}"` instead of `f"{pi:.2f}"`.

Option C: Support BOTH. Try .NET spec first; if it fails at runtime, try Python translation.

### Tests needed

```csharp
[Fact] FString_PythonFormatSpec_FixedPoint()  // f"{3.14159:.2f}" → "3.14"
[Fact] FString_PythonFormatSpec_ZeroPad()     // f"{42:08d}" → "00000042"
[Fact] FString_PythonFormatSpec_Hex()         // f"{255:#x}" → "0xff"
```

---

## ISSUE-5: Operator Dispatch Is Left-Biased (No `__radd__`)

**Severity:** MEDIUM — breaks common pattern
**File:** `src/Culebral.Compiler/IR/Lowering.cs`, BinaryExpr case (~line 1651)

### Problem

```python
class Meters:
    val: float = 0.0
    def __init__(v: float):
        @val = v
    def __mul__(scalar: float) -> Meters:
        return Meters(float(@val) * scalar)

m = Meters(5.0)
result = m * 3.0      # Works: left operand has __mul__
result = 3.0 * m      # FAILS: left operand is float (primitive), no __mul__
                       # Python would try m.__rmul__(3.0) — we don't
```

### Root cause

The operator dispatch at line ~1656 only checks `leftType` for dunder methods:
```csharp
var (foundType, _) = FindMethodInHierarchy(leftTypeName, dunderName);
```

Python's protocol: if `a.__add__(b)` returns `NotImplemented`, try `b.__radd__(a)`. Culebral skips the right-operand check entirely and falls through to raw CIL opcodes.

### Fix approach

After the left-type dunder check fails, add:
```csharp
// Reflected operator: check right operand
var reflectedDunder = "__r" + dunderName[2..]; // __add__ → __radd__
var (rightFoundType, _) = FindMethodInHierarchy(rightTypeName, reflectedDunder);
if (rightFoundType is not null)
{
    LowerExpression(bin.Right);  // right is receiver
    LowerExpression(bin.Left);   // left is argument
    _currentBlock!.Emit(new IrCallMethod(rightFoundType, reflectedDunder, 1, expr.Span));
    break;
}
```

### Tests needed

```csharp
[Fact] OperatorSyntax_RightMul_CallsRmul()  // 3 * MyObj() calls __rmul__
[Fact] OperatorSyntax_RightAdd_CallsRadd()   // 1 + MyObj() calls __radd__
```

---

## ISSUE-6: Class Declaration Order Affects Inheritance

**Severity:** MEDIUM — silent compilation failure
**File:** `src/Culebral.Compiler/Semantics/TypeChecker.cs` (CheckClass, _classFields)

### Problem

```python
class Dog(Animal):        # Dog declared BEFORE Animal
    def __init__(name: str):
        @name = name

class Animal:             # Animal declared AFTER Dog
    name: str = ""
```

The type checker processes classes in source order. When `Dog` is checked, `_classFields["Animal"]` doesn't exist yet (Animal hasn't been processed). Dog doesn't inherit Animal's fields. The type checker silently continues.

### Root cause

`_classFields` is populated during `CheckClass` (pass 2), which runs in source order. Inheritance field lookup at line ~290 uses `_classFields.TryGetValue(baseSt.Name, ...)` which returns false for forward-declared base classes.

### Fix approach

Option A: Two-pass class checking — first pass collects all class field declarations, second pass checks bodies with inheritance.

Option B: Sort classes topologically by inheritance before checking. Base classes are checked first.

Option C: Lazy field resolution — when a class needs base fields, check the base class on-demand if not yet processed.

### Test needed

```csharp
[Fact] Inheritance_ForwardDeclaredBase_FieldsAccessible()
// Dog(Animal) declared before Animal — @name should still be accessible
```

---

## ISSUE-7: Starred Unpacking Allocates Full Array Copy

**Severity:** LOW — performance only
**File:** `src/Culebral.Compiler/IR/Lowering.cs`, starred unpacking code (~line 2870)

### Problem

```python
a, *rest = [1, 2, 3, 4, 5]  # rest should be [2, 3, 4, 5]
```

The lowering emits `Enumerable.Cast<object>().ToArray()` to convert the RHS to `object[]` before indexing. This creates a full copy of the entire collection even if the collection is already an array or supports indexed access.

For large collections (10K+ elements), this is a significant allocation.

### Fix approach

Check if the source is already an `object[]` (from tuple construction) and skip the conversion. For `List<object>`, use `GetRange()` instead of copying the entire list to an array.

---

## ISSUE-8: No Integration Test for Web App Sample

**Severity:** LOW — regression risk
**File:** `samples/web-app/main.leb`

### Problem

The web app sample was tested manually once with `curl`. There's no automated test that:
1. Compiles the sample
2. Starts the server
3. Hits each endpoint
4. Verifies responses
5. Shuts down cleanly

If any compiler change breaks ASP.NET interop, the sample breaks silently.

### Fix approach

Add an integration test in the test project that:
```csharp
[Fact]
public void WebAppSample_Compiles()
{
    var source = File.ReadAllText("samples/web-app/main.leb");
    var diagnostics = new DiagnosticBag();
    var (bytes, _) = CulebralScript.CompileToBytes(source, diagnostics);
    Assert.False(diagnostics.HasErrors);
    Assert.True(bytes.Length > 0);
}
```

Full HTTP integration testing (start server + curl) is harder and may be flaky. At minimum, verify compilation succeeds.

---

## ISSUE-9: Error Messages Are Unhelpful

**Severity:** LOW — developer experience
**Files:** Multiple emitter/lowering paths

### Examples

| What the user sees | What it means | What they need to hear |
|---|---|---|
| `LEB4004: Cannot resolve method 'Upper' with 0 argument(s)` | `s.upper()` on an untyped variable | "Did you mean `s.upper()`? The method `upper` resolves to `ToUpper` on strings. Make sure the variable is a string." |
| `InvalidProgramException` | CIL stack imbalance | "Internal compiler error: generated invalid CIL. This is a bug — please report it." |
| `MissingMethodException: Method 'append' not found` | Called `.append()` on a non-list | "`.append()` is only available on lists. The receiver type could not be determined." |
| `LEB2003: Undefined name 'x'` | Variable used before assignment | "Variable 'x' is not defined. Did you mean to assign it first?" |

### Fix approach

Each diagnostic code should have a human-readable explanation with suggestions. Create a `DiagnosticMessages` class that maps codes to templates with context.

---

## ISSUE-10: Async State Machine Untested for Complex Patterns

**Severity:** LOW — may work, never verified
**File:** `src/Culebral.Compiler/Emit/CilEmitter.cs`, `EmitAsyncStateMachine` (~line 1898)

### Problem

The async state machine was inherited from earlier development. It handles basic `await` patterns. Never tested:

- `await` inside a `for` loop
- `await` inside `try/except`
- Multiple sequential `await`s (already tested: `Async_MultipleAwaits`)
- `await` in a nested function call: `print(await fetch(url))`
- `await` on a non-Task type (ValueTask, custom awaitables)

### Tests needed

```csharp
[Fact] Async_AwaitInForLoop()
[Fact] Async_AwaitInTryExcept()
[Fact] Async_NestedAwaitInCall()
```

---

## Summary

| # | Issue | Severity | Type | Effort |
|---|---|---|---|---|
| 1 | EmitVirtualCall special-case tower | HIGH | Architecture | High |
| 2 | Generator untested for complex control flow | HIGH | Testing | Medium |
| 3 | `with` statement broken | HIGH | Bug | Medium |
| 4 | F-string format specs wrong for Python syntax | MEDIUM | Compatibility | Medium |
| 5 | No `__radd__` reflected operators | MEDIUM | Feature | Low |
| 6 | Forward-declared base class breaks inheritance | MEDIUM | Bug | Medium |
| 7 | Starred unpacking allocates full copy | LOW | Performance | Low |
| 8 | No integration test for web sample | LOW | Testing | Low |
| 9 | Error messages unhelpful | LOW | DX | Medium |
| 10 | Async state machine untested for complex patterns | LOW | Testing | Medium |
