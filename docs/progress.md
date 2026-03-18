# Skelvon Compiler — Implementation Progress

## Status: Phase 2 Complete — Type System + Classes

The compiler implements a full pipeline: Source → Lexer → Parser → Type Checker → IR → CIL Emitter → .NET Assembly.

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

### CLI (`Program.cs`)
- `skelvon build <file.skv>` — compile to .dll
- `skelvon run <file.skv>` — compile and execute
- `skelvon check <file.skv>` — type-check only
- `skelvon lex <file.skv>` — debug token output
- `skelvon parse <file.skv>` — debug AST output
- `skelvon ir <file.skv>` — debug IR output

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

## Test Suite
- 113 tests total, all passing
- Lexer tests: 14 (tokens, literals, indentation, brackets, edge cases)
- Parser tests: 30 (all language constructs)
- Type checker tests: 10 (symbols, types, errors)
- End-to-end emit tests: 59 (compile → run → verify output)
  - Phase 1 core: 20 tests (functions, control flow, arithmetic, strings)
  - Phase 1 completion: 9 tests (mutual recursion, floats, nested loops, defaults, booleans, break/continue)
  - Phase 2 core: 9 tests (classes, constructors, fields, @field, interfaces)
  - Phase 2 completion: 10 tests (structs, records, properties, generics)
  - Phase 3: 11 tests (.NET interop, imports, case bridging, BCL access)

## Known Limitations / Next Steps

### Phase 3 (.NET Interop) — Remaining
- NuGet package resolution
- Extension methods (LINQ)
- .NET generic method calls

### Phase 4 (Language Completeness) — Next
**Core operators & statements (not in any prior phase):**
- `is` / `is not` operators (runtime type checks, null checks)
- `in` / `not in` operators (collection membership)
- `raise` statement (throw exceptions)
- `try`/`except`/`finally` (exception handling)
- `**` (power) and `//` (floor division) operator emission
- `range(start, stop)` and `range(start, stop, step)` overloads
- Multiple assignment / tuple unpacking (`a, b = b, a`)
- `*args` unpacking (maps to `params T[]`)
- `__str__` / `__repr__` → `ToString()` mapping

**Major language features:**
- Pattern matching with exhaustiveness (match/case codegen)
- Algebraic enum match codegen
- Async/await codegen
- Lambda expressions → delegates
- Comprehensions (list, dict, set, generator) full codegen
- `with` statement → IDisposable / using
- Null safety with flow typing
- Slicing (`items[1:3]`, `items[::-1]`)
- Named tuple returns and destructuring
- Record `with` expressions
- Type alias (`type X = Y`)
- Decorator emission (attributes and wrappers)
- Generic constraints enforcement
- True struct value-type semantics
- LSP server for editor support
