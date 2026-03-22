# Culebral Compiler — Implementation Status & Remaining Work

> Updated 2026-03-21 after comprehensive audit and implementation session.
> Compiler pipeline: Source → Lexer → Parser (AST) → TypeChecker → Lowering (AST→IR) → CilEmitter (IR→CIL)
> **All 366 tests pass.**

---

## Completion Summary

| Category | Status | Tests |
|---|---|---|
| Batch 1 — Core Operators & Statements | ✅ COMPLETE | Passing |
| Batch 2 — Missing Features | ✅ COMPLETE | Passing |
| Batch 3 — Advanced Type System | ✅ MOSTLY COMPLETE | Passing |
| Batch 4 — Async Runtime | ⚠️ Partial (stubs for async for/with) | Passing |
| Batch 5 — Tooling | ⚠️ Partial (PDB done, LSP exists) | Passing |
| Built-in Functions (30+) | ✅ ALL IMPLEMENTED | Passing |
| Operator Behavior Gaps (all 10) | ✅ ALL FIXED | Passing |
| Dunder Methods (27 mappings) | ✅ ALL MAPPED | Passing |
| String Method Aliases (12) | ✅ IMPLEMENTED | Passing |

---

## Decision Log — Excluded Features

| Feature | Reason |
|---|---|
| `del` statement (4.24) | Conflicts with static typing. Use `T?` + `None` instead. |
| `global` / `nonlocal` (4.26) | No module-level mutable state. Closures capture by reference (C# semantics). |
| Walrus operator `:=` (4.27) | Excluded for now. Use separate assignments. |

---

## What Was Implemented This Session

### New Built-in Functions (8 added)
- `hex(n)` → `"0x" + n.ToString("x")`
- `bin(n)` → `"0b" + Convert.ToString(n, 2)`
- `oct(n)` → `"0o" + Convert.ToString(n, 8)`
- `divmod(a, b)` → `(a / b, a % b)` as object[] tuple
- `pow(base, exp)` → `Math.Pow` (2-arg), `pow(base, exp, mod)` → modular (3-arg)
- `repr(x)` → strings get quotes (`'hello'`), others get `ToString()`
- `format(value, spec)` → `String.Format("{0:spec}", value)`
- `tuple(iterable)` → materializes to `object[]`

### New Dunder Method Mappings (16 added)
**Operators:** `__floordiv__` → `op_Division`, `__pow__` → `op_Exponent`, `__neg__` → `op_UnaryNegation`, `__pos__` → `op_UnaryPlus`, `__invert__` → `op_OnesComplement`, `__and__` → `op_BitwiseAnd`, `__or__` → `op_BitwiseOr`, `__xor__` → `op_ExclusiveOr`, `__lshift__` → `op_LeftShift`, `__rshift__` → `op_RightShift`

**Special methods:** `__setitem__` → `set_Item`, `__iter__` → `GetEnumerator`, `__call__` → `Invoke`, `__bool__` → `IsTrue`, `__enter__` → `Enter`, `__exit__` → `Exit`

**Unary operator support:** `EmitDunderOperator` now handles both unary (0 params → `op(T)`) and binary (1 param → `op(T, T)`) dunders.

### Python String Method Aliases (12 added)
`upper()→ToUpper()`, `lower()→ToLower()`, `strip()→Trim()`, `lstrip()→TrimStart()`, `rstrip()→TrimEnd()`, `startswith()→StartsWith()`, `endswith()→EndsWith()`, `find()→IndexOf()`, `rfind()→LastIndexOf()`, `replace()→Replace()`, `split()→Split()`, `zfill()→PadLeft()`

Applied at compile time in `EmitVirtualCall` and `ResolveVirtualCallReturnType`. Zero runtime overhead.

### New Tests Added (19)
- Hex, Bin, Oct, Divmod, Pow (2-arg and 3-arg), Repr (string and int), Format, Tuple (from list and empty)
- DunderNeg, DunderFloordiv, DunderPow (direct call)
- String aliases: Upper, Lower, Strip, Startswith, Find

---

## Genuinely Remaining Work

### Priority 1: Operator Syntax Dispatch for User Types

**What:** `a + b` where `a` is a user type with `__add__` should dispatch to `a.__add__(b)`. Currently, operator syntax on user types falls through to raw CIL opcodes (which crash for non-primitives). Direct method call syntax (`a.__add__(b)`) works.

**Where to fix:** `Lowering.cs` in `LowerExpression` → `BinaryExpr` case. After the existing collection-type checks, add a check: if either operand's type is a user-defined class with the relevant dunder method, emit `IrCallMethod` instead of `IrBinaryOp`.

**Mapping needed:**
```
BinaryOp.Add  → __add__     BinaryOp.Sub  → __sub__
BinaryOp.Mul  → __mul__     BinaryOp.Div  → __truediv__
BinaryOp.IntDiv → __floordiv__  BinaryOp.Mod → __mod__
BinaryOp.Pow  → __pow__     BinaryOp.Equal → __eq__
// etc.
```

Similarly for `UnaryExpr`: `Negate` → `__neg__`, `BitNot` → `__invert__`.

**Complexity:** MEDIUM. Need to look up dunder methods on the type from the type checker's resolved types.

**Tests to write:**
```csharp
[Fact] OperatorDispatch_PlusCallsAdd     // v1 + v2 calls __add__
[Fact] OperatorDispatch_MinusCallsSub    // -v calls __neg__
[Fact] OperatorDispatch_EqCallsEq        // v1 == v2 calls __eq__
```

---

### Priority 2: True `async for` / `async with`

**What:** Currently both parse and compile as synchronous equivalents. True async iteration needs `IAsyncEnumerable<T>` and `MoveNextAsync()`. True async with needs `IAsyncDisposable` and `DisposeAsync()`.

**Where to fix:**
- `Parser.cs`: Already parses `async for` and `async with` (desugars to sync)
- `Lowering.cs`: Need separate lowering paths that emit `IrAwait` around `MoveNextAsync` and `DisposeAsync`
- `CilEmitter.cs`: `IrAwait` emission already works

**Depends on:** Async/await (already working).
**Complexity:** MEDIUM-HIGH.

---

### Priority 3: Generic Constraint Emission to Assembly

**What:** Generic constraints are parsed and enforced at the type-checker level but not emitted to the assembly metadata. The compiler uses type erasure (T → object). Emitting CLR-level constraints would enable better JIT optimization and cross-assembly constraint validation.

**Where to fix:** `CilEmitter.cs` — call `DefineGenericParameters()` on TypeBuilder, then `SetGenericParameterConstraints()` for each constrained parameter.

**Complexity:** HIGH. Requires changing field/method type resolution from `object` to `GenericTypeParameterBuilder`.
**Note:** This is a deliberate design decision (type erasure). May not be worth changing unless cross-assembly generic interop is needed.

---

### Priority 4: Python `str.join()` Special Handling

**What:** Python's `", ".join(items)` → .NET's `string.Join(", ", items)`. This requires swapping receiver and argument and calling a static method instead of instance.

**Where to fix:** Special case in `EmitVirtualCall` or in the lowering when `join` is called on a string.

**Complexity:** LOW.

---

### Priority 5: Tooling (Batch 5 remaining)

- `culebral fmt` — formatter exists, needs refinement
- `culebral test` — test runner exists, needs expansion
- REPL — exists, needs polish

---

## Architecture Notes

### Files Modified This Session

| File | Changes |
|---|---|
| `src/Culebral.Compiler/Emit/CilEmitter.cs` | Added 8 builtins (hex/bin/oct/divmod/pow/repr/format/tuple), 16 dunder mappings, Python string aliases, unary operator support |
| `src/Culebral.Compiler/Semantics/SymbolTable.cs` | Registered 8 new builtins in type checker |
| `src/Culebral.Compiler/IR/Lowering.cs` | Added 8 builtins to known-builtins set |
| `tests/Culebral.Compiler.Tests/EmitTests.cs` | Added 19 new end-to-end tests |
| `spec.md` | Comprehensive status update for all features |

### Key Design Decisions
- **Type erasure for generics**: User-defined generics compile with `T → object`. This simplifies the emitter but means generic constraints aren't in assembly metadata.
- **Dunder dispatch**: Dunders are emitted as both instance methods (for direct call) and static operator methods (for .NET interop). Operator syntax dispatch is not yet wired through the lowering.
- **String aliases**: Compile-time resolution, zero runtime overhead. Table-based lookup in `PythonStringAliases` dictionary.
