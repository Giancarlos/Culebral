# Culebral Compiler — Implementation Progress

## Status: Phase 3 Complete — .NET Interop

The compiler implements a full pipeline: Source → Lexer → Parser → Type Checker → IR → CIL Emitter → .NET Assembly.

### Phase 3 Additions (Complete)
- **BCL imports**: `from System.IO import File` resolves via reflection
- **Namespace aliases**: `import System.IO as io` with chained resolution
- **Static method calls**: `File.read_all_text(path)` with snake_case bridging
- **Instance method calls**: `sb.append("text")` on .NET type instances
- **Property access**: static and instance properties via snake→pascal resolution
- **Constructor resolution**: `StringBuilder()` → `newobj` with arg count matching
- **Case convention bridging**: `read_all_text` ↔ `ReadAllText` bidirectional
- **Generic method calls**: `Array.empty[int]()` → `MakeGenericMethod` with type arg resolution
- **Extension methods (LINQ)**: `items.count()`, `items.first()`, `items.any()` from `System.Linq.Enumerable`
- **Extension method type inference**: auto-infers generic type args from receiver type
- **Auto-boxing for extension results**: value type returns auto-boxed for object locals
- **NuGet package resolution**: `culebral.toml` parsing → `dotnet restore` → assembly loading
- **List literal boxing**: value types auto-boxed when added to `List<object>`

### Phase 2 Additions
- **Class instantiation**: `c = Counter(10)` → `newobj .ctor`
- **Constructors**: `__init__` methods compile to CIL `.ctor` with proper base class chaining
- **Instance fields**: automatic `this` threading through all instance methods
- **Field access**: bare names (`count`) resolve to fields in methods; `@field` syntax for disambiguation when parameters shadow fields
- **Field mutation**: `count += 1`, `@name = value` — augmented and direct assignment to fields
- **Instance method calls**: `obj.method(args)` → proper `callvirt` with type resolution
- **Default field values**: `count: int = 0` initialized in constructors
- **Interface implementation**: `class Dog(Describable):` with proper CLR interface mapping
- **Interface definitions**: abstract method declarations, null parent type
- **Multiple instances**: each instance has independent field state

## What Works (Phase 1 — MVP)

### Lexer (`Lexer/`)
- Indentation-aware tokenization with INDENT/DEDENT tokens
- All operators, delimiters, keywords
- Integer literals (decimal, hex, binary, octal with `_` separators)
- Float literals (decimal, scientific notation)
- String literals with escape sequences
- F-string literals
- Triple-quoted strings
- `@field` identifier tokens
- Comment skipping
- Bracket-aware newline suppression (no INDENT/DEDENT inside `()`, `[]`, `{}`)

### Parser (`Parser/`)
- Full recursive descent parser
- Function definitions (with typed params, return types, async)
- Class, struct, record, enum, interface definitions
- Property definitions (`prop name -> type: get/set`)
- if/elif/else, while, for, break, continue, pass
- match/case with patterns (wildcards, literals, constructors, names)
- try/except/finally, raise
- with statement
- import / from-import with dotted names
- when target conditional compilation
- Expressions: binary, unary, call, member access, index, slice
- List, dict, set, tuple literals
- List/dict comprehensions and generator expressions
- Lambda expressions
- Conditional expressions (ternary)
- f-string interpolation parsing
- Type annotations: simple, generic, nullable, tuple
- Decorators
- Abstract method declarations (no body)

### Type Checker (`Semantics/`)
- Two-pass checking (declarations then bodies)
- Built-in types: int, long, float, bool, str, byte, char, object, void
- Built-in functions: print, len, range, int, float, str, etc.
- Type annotation resolution (simple, generic, nullable, tuple)
- Local variable type inference from assignments
- Function signature type checking
- Class/struct/record/enum/interface registration
- Undefined name detection
- Type compatibility checking (assignment, widening)
- Generic method return type inference via `MakeGenericMethod`

### IR (`IR/`)
- Basic block-based intermediate representation
- Stack-based instruction set
- Typed locals with proper type propagation
- Function lowering with parameters and locals
- Control flow: if/elif/else, while, for (desugared to enumerator)
- Break/continue with loop context
- Type definitions: class, struct, record, enum (sealed hierarchy), interface
- Property lowering to getter/setter methods
- String concatenation type tracking
- Top-level statement wrapping (script mode)
- .NET interop instructions: generic calls, extension methods

### CIL Emitter (`Emit/`)
- PersistedAssemblyBuilder with ManagedPEBuilder for assembly generation
- Runtime config generation for `dotnet` execution
- Type emission: classes, structs, interfaces, sealed hierarchies
- Method emission with ILGenerator
- Optimized local load/store (short forms for indices 0-3)
- Optimized integer constants (Ldc_I4_0 through Ldc_I4_8)
- Built-in function mapping (print → Console.WriteLine with type overloads)
- String concatenation via String.Concat
- Arithmetic, comparison, bitwise operators
- Control flow: branches, conditional branches
- Stack type inference for correct method overload selection
- Entry point configuration
- Generic method emission via `MakeGenericMethod`
- Extension method emission as static calls with auto-boxing

### CLI (`Program.cs`)
- `culebral build <file.leb>` — compile to .dll
- `culebral run <file.leb>` — compile and execute
- `culebral check <file.leb>` — type-check only
- `culebral lex <file.leb>` — debug token output
- `culebral parse <file.leb>` — debug AST output
- `culebral ir <file.leb>` — debug IR output
- Auto-discovery of `culebral.toml` for NuGet resolution

### NuGet (`NuGet/`)
- `culebral.toml` parsing: `[project]` and `[dependencies]` sections
- Package version strings and framework references
- Temp .csproj generation → `dotnet restore` for resolution
- Asset file parsing to discover assembly paths
- Assembly loading for compile-time type resolution

## Verified Working Programs

### Phase 1 (Functions + Control Flow)
- Hello World
- Fibonacci (recursive)
- Factorial (recursive)
- Integer and float arithmetic (all operators)
- String concatenation (`+` operator)
- F-string interpolation (`f"Hello, {name}!"`)
- Conditional branching (if/elif/else, nested)
- While loops with augmented assignment
- For loops with `range()`, including nested
- Break and continue in loops
- Mutual recursion (forward references between functions)
- Default parameter values
- Boolean logic (`and`, `or`, `not`)
- All comparison operators (`==`, `!=`, `<`, `>`, `<=`, `>=`)
- Type-annotated variables
- Nested function calls
- Multi-parameter functions
- String return values

### Phase 2 (Classes + Types)
- Class instantiation with constructor parameters
- Instance fields with default values
- Instance method calls
- Field access (bare names and `@field` disambiguation)
- Field mutation (direct and augmented assignment)
- Multiple independent instances
- F-strings with field interpolation
- Interface definitions and implementation
- Classes implementing multiple interfaces

### Phase 3 (.NET Interop)
- BCL static method calls with case bridging
- Namespace aliases and chaining
- .NET constructors and instance methods
- File I/O, Math, Environment, StringBuilder
- String instance methods (to_upper, contains, replace, etc.)
- Generic static methods (`Array.empty[int]()`, `Activator.create_instance[T]()`)
- LINQ extension methods (`count`, `first`, `last`, `any`, `contains`, `to_array`)

### Phase 4 Batch 1 — Core Operators (Complete)
- `is` / `is not` operators (runtime type checks and null checks)
- `in` / `not in` operators (collection membership)
- `raise` statement (throw exceptions)
- `try`/`except`/`finally` (exception handling)
- `**` (power) and `//` (floor division) operator emission
- `range(start, stop)` and `range(start, stop, step)` overloads
- `__str__` → `ToString()` mapping
- Pattern matching with algebraic enum dispatch

### Phase 4 Batch 2 — Language Features (Complete)
- **Lambda expressions** → delegate emission with `IrCreateDelegate`
- **`with` statement** → `IDisposable` / `try-finally` desugaring
- **Set literals** → `HashSet<object>` creation
- **Dict literals** → `Dictionary<object, object>` creation
- **Dict comprehensions** → dictionary construction with loop
- **Slicing** → `list[a:b]`, `string[a:b]` with runtime helper
- **Tuple unpacking** → `a, b = b, a` with correct swap semantics
- **`*args` variadic parameters** → `params object[]` with `[ParamArray]`
- **Record `with` expressions** → `p1 with (x=10)` creates modified copy
- **Type aliases** → `type Count = int` (compile-time erasure)
- **Explicit type casts** → `IrCastClass` emission
- **Delegate invocation** → `IrInvokeDelegate` for calling function parameters

### Phase 4 Batch 3 — Advanced Type System (Complete)
- **Operator overloading** — 16 dunder methods: `__eq__`, `__ne__`, `__lt__`, `__le__`, `__gt__`, `__ge__`, `__add__`, `__sub__`, `__mul__`, `__truediv__`, `__mod__`, `__hash__`, `__len__`, `__getitem__`, `__contains__`, `__str__`
- **Comparison chaining** → `a < b < c` desugared to `a < b and b < c`
- **Generic constraints enforcement** → `T: Printable` checked at instantiation
- **Default interface implementations** → inherited by implementing classes
- **True struct value-type semantics** → inherits from `System.ValueType`, sequential layout
- **Decorator emission** → .NET attributes applied via `CustomAttributeBuilder`
- **For-else / while-else** → else block runs if loop completes without break
- **`yield` statement** → generator functions returning `IEnumerable<object>`
- **`assert` statement** → conditional throw with optional message
- **Built-in functions** → `abs`, `min`, `max`, `chr`, `ord`, `type`, `input`, `round`

### Phase 4 Batch 4 — Python Compatibility (Complete)
- **Truthiness** → `if items:`, `while queue:`, `and`/`or`/`not` on non-bool types
- **Negative indexing** → `items[-1]` returns last element
- **True division** → `10 / 3` returns `3.333...` (float), `//` for integer
- **List concatenation** → `[1,2] + [3,4]` returns `[1,2,3,4]`
- **List/string repetition** → `[0] * 5`, `"ha" * 3`
- **String `in`** → `"bc" in "abcd"` (substring check)
- **List extend** → `items += [4, 5]` calls AddRange
- **Chained assignment** → `a = b = c = 0`
- **Multiple except types** → `except (ValueError, TypeError)`
- **Call-site unpacking** → `f(*args)`
- **Dict merge** → `d1 | d2`
- **Float promotion** → `2 ** -1` returns `0.5`
- **Multi-type generics** → `Dictionary[str, int]()`

### Phase 4 Batch 5 — Advanced Features (Complete)
- **Async/await** → Phase 1 synchronous via `.GetAwaiter().GetResult()`
- **Null safety flow typing** → type narrowing after `if x is None: return`
- **Named tuple returns** → `-> (name: str, age: int)` with `.name` access
- **Built-in Result/Ok/Err** → algebraic error handling without imports
- **Python-compatible print()** → multi-arg, sep, end, flush, file
- **Fixed broken stubs** → bool, sorted, enumerate, zip, map, filter, isinstance
- **Added missing builtins** → all, any, sum, list, dict, hash, reversed
- **Fixed overloads** → min/max iterable, range negative step, round ndigits, int base

### Phase 4 Batch 6 — Tooling (Complete)
- **PDB debug info** → portable PDB with sequence points for step-through debugging
- **REPL** → `culebral repl` interactive mode with multi-line input
- **Formatter** → `culebral fmt [--check] <file.leb>` with whitespace/blank line rules
- **Test runner** → `culebral test <file.leb>` discovers and runs `test_` functions
- **Embedding MVP** → `Culebral.Scripting` with CulebralEngine, Execute, Eval, global injection

## Test Suite
- 319 tests total (318 passing, 1 skipped)
- Lexer tests: 14
- Parser tests: 45
- Type checker tests: 15
- Formatter tests: 14
- Scripting tests: 15
- End-to-end emit tests: 216

## Known Limitations / Next Steps

### Remaining
- LSP server for editor support
- `async for` / `async with`
- True async state machines (currently synchronous)
- Host function injection in embedding (API surface exists, wiring needed)
- `culebral.testing` standard library module

### Phase 5 — Standard Library (minimal — .NET interop IS the stdlib)
### Phase 6 — Native Modules (LLVM)
