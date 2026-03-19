# Culebral — A Python-Inspired Language for .NET

> The skeleton of Python, forged for .NET.

## Design Philosophy

1. **Python's readability, Rust's discipline, .NET's power.**
2. **Types at boundaries, inference inside.** Function signatures are contracts. Bodies are implementation.
3. **If you write a type, you mean it.** No optional hints — every annotation is enforced.
4. **Interop is not an afterthought.** Calling a NuGet package should feel native.
5. **Earn your complexity.** No metaclasses, no descriptor protocol, no MRO diamonds. If a feature doesn't pull its weight, it's out.

---

## Syntax Overview

### Hello World

```python
def main():
    print("Hello, World!")
```

Top-level statements are allowed (script mode). The compiler looks for a `main()` if present, otherwise wraps top-level code.

### Functions — The Core Rule

**Parameters and return types are always explicit. Local variables are inferred.**

```python
def greet(name: str, times: int) -> str:
    # 'parts' is inferred as list[str]
    parts = [f"Hello, {name}!" for _ in range(times)]
    # 'result' is inferred as str
    result = "\n".join(parts)
    return result

# Void return is implicit (like Python), compiles to void
def log(message: str):
    print(message)
```

This is the Rust rule: signatures are the API contract. The compiler has everything it needs to infer the body.

### Variables and Type Binding

```python
# Inferred — compiler figures it out
x = 42              # int (System.Int32)
name = "Alice"      # str (System.String)
items = [1, 2, 3]   # list[int] (List<int>)

# Explicit — you're locking it in, this IS that type
x: float = 42       # float (System.Double), not int
items: list[object] = [1, 2, 3]  # list[object], not list[int]

# Reassignment respects the bound type
x = 42
x = "hello"  # COMPILE ERROR: x was inferred as int
```

### No `self` Parameter

```python
class Counter:
    count: int = 0  # Instance field with default

    def __init__(initial: int = 0):
        count = initial  # Bare name refers to field when unambiguous

    def increment() -> int:
        count += 1
        return count

    def reset():
        count = 0  # Refers to field 'count'

    def display(count: int):
        # Parameter shadows field — use @count for field
        print(f"Parameter: {count}, Field: {@count}")
```

**Field access rule:** Bare names resolve to the nearest scope. Use `@` prefix for explicit field access when shadowed (similar to Ruby, short and clean). This replaces `self.` everywhere.

### Properties (First-Class)

```python
class Temperature:
    _celsius: float

    def __init__(celsius: float):
        _celsius = celsius

    # Property syntax — clean, no decorators needed
    prop celsius -> float:
        get: return _celsius
        set: _celsius = value

    prop fahrenheit -> float:
        get: return _celsius * 9.0 / 5.0 + 32.0
        set: _celsius = (value - 32.0) * 5.0 / 9.0

    # Read-only shorthand
    prop kelvin -> float:
        get: return _celsius + 273.15
```

### Enums (Algebraic, Like Rust)

```python
enum Shape:
    Circle(radius: float)
    Rectangle(width: float, height: float)
    Point  # No data

# Usage
s = Shape.Circle(5.0)

match s:
    case Shape.Circle(r):
        print(f"Circle with radius {r}")
    case Shape.Rectangle(w, h):
        print(f"Rectangle {w}x{h}")
    case Shape.Point:
        print("Just a point")
```

Compiles to a sealed class hierarchy on .NET — `Shape` is abstract, each variant is a sealed subclass.

### Pattern Matching (Enhanced)

```python
def describe(value: object) -> str:
    match value:
        case int(n) if n > 0:
            return "positive int"
        case str(s) if len(s) > 10:
            return "long string"
        case list(items) if len(items) == 0:
            return "empty list"
        case None:
            return "nothing"
        case _:
            return "something else"
```

### Null Safety

No implicit nulls. Use `?` for nullable types (like C# nullable refs, but enforced).

```python
def find_user(id: int) -> User?:
    if id in database:
        return database[id]
    return None

def greet_user(id: int) -> str:
    user = find_user(id)

    # user is User? here — must handle None
    if user is None:
        return "Unknown"

    # user is narrowed to User here (flow typing)
    return f"Hello, {user.name}"

    # This would be a compile error:
    # return user.name  # ERROR: user might be None
```

### Async/Await (Maps to .NET Tasks)

```python
async def fetch_data(url: str) -> str:
    client = HttpClient()
    response = await client.get_async(url)
    return await response.content.read_as_string_async()

async def fetch_all(urls: list[str]) -> list[str]:
    tasks = [fetch_data(url) for url in urls]
    return await Task.when_all(tasks)
```

### Error Handling — Result Type + Exceptions

```python
# Use Result for expected failures
from culebral.result import Result, Ok, Err

def parse_int(s: str) -> Result[int, str]:
    try:
        return Ok(int(s))
    except ValueError:
        return Err(f"Cannot parse '{s}' as int")

# Pattern match on results
match parse_int(input_str):
    case Ok(n):
        print(f"Got {n}")
    case Err(msg):
        print(f"Failed: {msg}")

# .NET exceptions still work for exceptional cases
def read_file(path: str) -> str:
    # IOException propagates naturally
    with open(path) as f:
        return f.read()
```

### Structs (Value Types)

```python
struct Point:
    x: float
    y: float

    def distance_to(other: Point) -> float:
        return ((x - other.x) ** 2 + (y - other.y) ** 2) ** 0.5

# Structs are value types — stack allocated, copied on assignment
p1 = Point(1.0, 2.0)
p2 = p1       # Copy, not reference
p2.x = 99.0   # p1.x is still 1.0
```

### Interfaces (Replacing Protocols/ABCs)

```python
interface Drawable:
    def draw(canvas: Canvas) -> None
    def bounds() -> Rect

# Default implementations allowed
interface Printable:
    def to_string() -> str

    def print():
        print(to_string())  # Default impl

class Circle(Drawable, Printable):
    radius: float

    def draw(canvas: Canvas) -> None:
        canvas.draw_circle(0, 0, radius)

    def bounds() -> Rect:
        return Rect(-radius, -radius, radius * 2, radius * 2)

    def to_string() -> str:
        return f"Circle(r={radius})"
```

### Generics

```python
class Stack[T]:
    _items: list[T]

    def __init__():
        _items = []

    def push(item: T):
        _items.append(item)

    def pop() -> T?:
        if len(_items) == 0:
            return None
        return _items.pop()

# Constrained generics
def largest[T: Comparable](items: list[T]) -> T:
    result = items[0]
    for item in items[1:]:
        if item > result:
            result = item
    return result
```

### `with` Statement (IDisposable)

```python
# Maps directly to IDisposable / using
with open("data.txt") as f:
    content = f.read()

# Multiple resources
with SqlConnection(conn_str) as conn, conn.begin_transaction() as tx:
    conn.execute("INSERT INTO ...")
    tx.commit()
```

### Decorators (Compile-Time Attributes)

```python
@route("/api/users/{id}")
@authorize("admin")
async def get_user(id: int) -> User:
    return await user_service.find(id)

# Decorators compile to .NET attributes where possible,
# or to wrapper functions (like Python) when they need runtime behavior
```

### Comprehensions (Kept from Python)

```python
# List comprehension — compiles to efficient .NET code
squares = [x ** 2 for x in range(10) if x % 2 == 0]

# Dict comprehension
word_lengths = {word: len(word) for word in words}

# Generator expression — compiles to IEnumerable<T> with yield
evens = (x for x in range(1000) if x % 2 == 0)
```

### Tuples (Named and Positional)

```python
# Positional — maps to System.ValueTuple
def divmod(a: int, b: int) -> (int, int):
    return (a // b, a % b)

q, r = divmod(17, 5)

# Named — maps to named ValueTuple fields
def get_user_info(id: int) -> (name: str, age: int, active: bool):
    return (name="Alice", age=30, active=True)

info = get_user_info(1)
print(info.name)
```

---

## .NET Interop

### The Golden Rule

Any .NET type is usable. Any NuGet package is importable. The language auto-bridges conventions.

### Import Mapping

```python
# .NET namespaces map to Python-style imports
from System.Collections.Generic import Dictionary, List
from System.Net.Http import HttpClient
from Microsoft.AspNetCore.Builder import WebApplication

# Or import the namespace
import System.IO as io
content = io.File.read_all_text("data.txt")
```

### Case Convention Bridging

.NET uses PascalCase. Culebral code uses snake_case. The compiler bridges both directions automatically.

```python
from System.Net.Http import HttpClient

async def fetch(url: str) -> str:
    client = HttpClient()
    # You write snake_case, compiler maps to PascalCase
    response = await client.get_async(url)
    content = await response.content.read_as_string_async()
    return content

    # The following ALSO works — PascalCase is always accepted
    # response = await client.GetAsync(url)
```

**Rules:**
- `get_async` → `GetAsync` (auto-mapped)
- `PascalCase` always accepted verbatim (no mapping needed)
- Properties: `response.status_code` → `response.StatusCode`
- Events: `button.click += handler` works naturally

### NuGet Integration

```toml
# culebral.toml (project file)
[project]
name = "my-app"
version = "0.1.0"
target = "net10.0"

[dependencies]
"Newtonsoft.Json" = "13.0.3"
"Dapper" = "2.1.*"
"Microsoft.AspNetCore.App" = { framework = true }
```

```python
# Then just use it
from Newtonsoft.Json import JsonConvert

data = JsonConvert.deserialize_object[MyType](json_str)
```

### Extension Methods

```python
# .NET extension methods are available as methods
from System.Linq import Enumerable  # Brings LINQ extensions into scope

numbers = [3, 1, 4, 1, 5, 9]
result = numbers.where(lambda x: x > 3).order_by(lambda x: x).to_list()

# Can also use comprehension style — compiler optimizes both
result = sorted([x for x in numbers if x > 3])
```

### Delegates and Events

```python
# Lambda syntax uses Python's lambda or block syntax
from System import Action, Func

# Short lambdas (like Python)
action: Action[int] = lambda x: print(x)

# Multi-line lambdas (Python doesn't have these — we do)
transform: Func[int, str] = lambda x:
    result = x * 2
    return f"Value: {result}"

# Events
button.click += lambda sender, args: print("Clicked!")
```

---

## What We Keep from Python

| Feature | Status | Notes |
|---|---|---|
| Indentation-based blocks | **Kept** | Core identity |
| f-strings | **Kept** | `f"Hello, {name}"` |
| Comprehensions | **Kept** | Compile to efficient .NET |
| Slicing | **Kept** | `items[1:3]`, `items[::-1]` |
| `for x in collection` | **Kept** | Maps to `IEnumerable<T>` |
| `with` statement | **Kept** | Maps to `IDisposable` |
| Multiple assignment | **Kept** | `a, b = b, a` |
| `*args` unpacking | **Kept** | Maps to `params T[]` |
| Decorators | **Kept** | Maps to attributes or wrappers |
| `__init__` | **Kept** | Constructor syntax |
| `__str__`, `__repr__` | **Kept** | Map to `ToString()` etc |
| `range()` | **Kept** | But compiled, not interpreted |

## What We Drop

| Feature | Reason |
|---|---|
| `self` parameter | Implicit — use `@field` for disambiguation |
| Dynamic typing | Types at boundaries, inference inside |
| `**kwargs` | Use typed optional params or config objects |
| Metaclasses | Complexity not worth it — use decorators + interfaces |
| Multiple inheritance | Single inheritance + interfaces (like C#/Rust) |
| GIL | .NET threading model — real parallelism |
| Duck typing | Interfaces replace this with compile-time safety |
| `__slots__` | All classes have defined layouts (it's compiled) |
| Monkey patching | No runtime type mutation |
| `eval()` / `exec()` | Security and performance — use source generators if needed |

## What We Change

| Python | Culebral | Rationale |
|---|---|---|
| `None` | `None` (but nullable types) | Must declare `T?` to allow None |
| `class` | `class` / `struct` / `enum` | Separate value types and sum types |
| `typing.Protocol` | `interface` | First-class, not bolted on |
| `@property` | `prop` keyword | Cleaner syntax |
| `abc.ABC` | `interface` | One mechanism, not three |
| `dataclasses` | `struct` or `record` | Built into the language |
| `match` | `match` (enhanced) | Exhaustiveness checking, type narrowing |
| `lambda` | `lambda` (multi-line) | Finally, proper lambdas |

---

## Records (Data Classes Done Right)

```python
# Immutable by default, structural equality, with-expressions
record User:
    name: str
    email: str
    age: int

alice = User("Alice", "alice@example.com", 30)
bob = alice with (name="Bob", email="bob@example.com")

# Records can have methods
record Point:
    x: float
    y: float

    def magnitude() -> float:
        return (x ** 2 + y ** 2) ** 0.5
```

Compiles to C# `record` types — immutable, value equality, `with` expressions.

---

## ASP.NET Example (Feels Like Flask/FastAPI)

```python
from Microsoft.AspNetCore.Builder import WebApplication
from culebral.web import get, post, body, path

app = WebApplication.create()

@get("/")
def index() -> str:
    return "Hello, World!"

@get("/users/{id}")
async def get_user(id: int) -> User?:
    return await db.find_user(id)

@post("/users")
async def create_user(user: User via body) -> User:
    return await db.insert(user)

app.run()
```

The `via body` / `via path` / `via query` syntax tells the compiler where to bind parameters from — cleaner than FastAPI's `Depends()` magic.

---

## Conditional Compilation

The `when target` construct enables platform-conditional code. The compiler evaluates the condition **before** type checking and discards the dead branch entirely. This is the foundation for the standard library and future native module support.

```python
when target == "net":
    from System.IO import File as _File

    def read_text(path: str) -> str:
        return _File.read_all_text(path)

when target == "native":
    from culebral.ffi.libc import fopen, fread, fclose

    def read_text(path: str) -> str:
        # native implementation via C FFI
        ...
```

Valid targets: `"net"` (primary, default), `"native"` (future LLVM backend). User code can branch on target, but in practice this should live almost entirely in library code — application code should use `culebral.*` abstractions.

---

## Standard Library (`culebral.*`)

Culebral's standard library is deliberately minimal. .NET interop with automatic case bridging (`snake_case` in Culebral maps to `PascalCase` in .NET) means the entire BCL and NuGet ecosystem is directly usable without wrappers. There is no need for `culebral.io`, `culebral.collections`, `culebral.json`, etc. — users import `System.IO`, `System.Collections.Generic`, `System.Text.Json` directly and call them with Pythonic naming conventions.

The `culebral.*` namespace is reserved for the few things .NET does not already provide.

### Built-in Types

**`Result`** — A built-in algebraic type for error handling without exceptions. `Ok(value)` and `Err(value)` are constructors available in all Culebral programs without imports.

```python
def divide(a: int, b: int) -> Result:
    if b == 0:
        return Err("division by zero")
    return Ok(a // b)

r = divide(10, 2)
print(r.is_ok)   # True
print(r.value)    # 5
print(r.is_err)   # False
```

Properties: `is_ok` (bool), `is_err` (bool), `value` (the wrapped value).

### Module Resolution

Import resolution follows a search path: project source → `lib/` directory → standard library path. `culebral.*` imports resolve to `.cbl` files on disk. .NET imports (anything else like `System.*`, `Microsoft.*`, NuGet packages) resolve against .NET assembly metadata.

### Future Modules

| Module | Purpose | Priority |
|---|---|---|
| `culebral.testing` | Built-in test runner for `culebral test` | When `culebral test` is implemented |

---

## Compiler Architecture

The compiler is split at the **CulebralIR** boundary. Everything above the IR is shared across all backends. Everything below is target-specific. This costs almost nothing to implement versus emitting CIL directly, but keeps the door open for future backends.

```
Source (.cbl files)
    │
    ▼
┌──────────────┐
│    Lexer     │  Indentation → INDENT/DEDENT tokens
└──────┬───────┘
       ▼
┌──────────────┐
│    Parser    │  Recursive descent → AST
└──────┬───────┘
       ▼
┌──────────────┐
│  Conditional │  Evaluate `when target` blocks, discard dead branches
│  Compilation │
└──────┬───────┘
       ▼
┌──────────────┐
│  Name Res /  │  Resolve names, imports, .NET type references
│  Type Check  │  Infer locals, check signatures, null safety
└──────┬───────┘
       ▼
┌──────────────┐
│  Lowering    │  Desugar comprehensions, with, pattern matching
└──────┬───────┘
       ▼
┌──────────────┐
│  CulebralIR   │  Typed, lowered, target-agnostic representation
└──────┬───────┘
       │
       ├──────────────────────────┐
       ▼                          ▼
┌──────────────┐          ┌──────────────┐
│  .NET CIL    │          │  LLVM        │  ⚠ FUTURE — LOWEST PRIORITY
│  Emitter     │          │  Emitter     │
└──────┬───────┘          └──────┬───────┘
       ▼                          ▼
  .dll / .exe               .so / .dll / .dylib
  (runs on .NET)            (native binary)
```

### CulebralIR

The intermediate representation sits between the type-checked AST and code emission. It should be lower-level than the AST but target-agnostic:

- Basic blocks with typed instructions
- Function calls with calling convention annotations
- Struct layouts with explicit field offsets
- Monomorphized generics (concrete types stamped out)
- Clear distinction between heap-allocated (class) and stack-allocated (struct) values
- Async state machines lowered to explicit state/switch form

The IR should feel closer to LLVM IR than CIL in spirit — lower is easier to raise than the reverse. The .NET emitter can ignore layout information the CLR manages itself.

### Target Platform

**.NET 10 (LTS)** — released November 11, 2025, supported until November 2028. The compiler itself targets .NET 10, and generated assemblies target `net10.0`.

### Implementation Language

**C# 14 / .NET 10 for the compiler.** The entire compiler is a .NET 10 console application with zero external dependencies for core functionality.

### .NET Built-In Libraries Used

The compiler takes full advantage of .NET's built-in tooling. No third-party NuGet packages are required for core compilation.

**`System.Reflection.Emit` — IL Emission (Primary Backend)**

`PersistedAssemblyBuilder` (new in .NET 9, stable in .NET 10) is the core of the CIL emitter. It provides a fully managed `Reflection.Emit` implementation that can save assemblies to disk — previously this required Windows-specific native code or third-party libraries like Mono.Cecil. The API chain is: `PersistedAssemblyBuilder` → `ModuleBuilder` → `TypeBuilder` → `MethodBuilder` → `ILGenerator`. The `ILGenerator` emits CIL opcodes (`OpCodes.Ldstr`, `OpCodes.Call`, `OpCodes.Ret`, etc.) and the assembly is saved with `ab.Save("output.dll")`.

Key types: `PersistedAssemblyBuilder`, `ModuleBuilder`, `TypeBuilder`, `MethodBuilder`, `FieldBuilder`, `PropertyBuilder`, `ILGenerator`, `OpCodes`, `Label`, `LocalBuilder`.

**`System.Reflection.Metadata` — Assembly Reading**

Used to inspect existing .NET assemblies during compilation — resolving types from BCL and NuGet packages without loading them into the runtime. When the compiler encounters `from System.Net.Http import HttpClient`, it reads the assembly metadata to find the type, its methods, parameter types, and generic signatures. This is the read side of the compiler's .NET interop. Lower-level than Reflection but fast and allocation-friendly.

Key types: `MetadataReader`, `PEReader`, `TypeDefinition`, `MethodDefinition`, `AssemblyReference`.

**`System.Reflection.PortableExecutable` — PE File Control**

Works with `PersistedAssemblyBuilder` for fine-grained control over the output binary: setting entry points, PE headers, subsystem type (console vs. GUI), and debug directory entries for PDB generation.

Key types: `PEBuilder`, `ManagedPEBuilder`, `PEHeaderBuilder`, `DebugDirectoryBuilder`.

**`System.Reflection` — Runtime Type Resolution**

Used to resolve well-known BCL types during compilation. When Culebral's `str` maps to `System.String`, or `print()` maps to `Console.WriteLine()`, the compiler resolves these through `typeof(string)`, `typeof(Console).GetMethod(...)`, etc.

### Optional NuGet Dependencies

These are not required but may be added for convenience:

| Package | Purpose | When to Add |
|---|---|---|
| `System.CommandLine` | CLI argument parsing (`culebral build`, `culebral run`) | Phase 1 — nice to have |
| `xUnit` + `FluentAssertions` | Testing framework | Phase 1 — essential |
| `Mono.Cecil` | Richer API for reading assembly metadata | Phase 3 — only if `System.Reflection.Metadata` proves too low-level |

### Project Structure

```
culebral/
├── src/
│   └── Culebral.Compiler/              # Main compiler — .NET 10 console app
│       ├── Culebral.Compiler.csproj     # <TargetFramework>net10.0</TargetFramework>
│       ├── Program.cs                  # CLI entry: culebral build / run / check
│       ├── Lexer/
│       │   ├── Token.cs                # Token type enum + Token struct
│       │   ├── Lexer.cs                # Indentation-aware tokenizer
│       │   └── SourceLocation.cs       # File, line, column tracking
│       ├── Parser/
│       │   ├── Ast.cs                  # AST node type hierarchy
│       │   └── Parser.cs              # Recursive descent parser
│       ├── Semantics/
│       │   ├── TypeChecker.cs          # Type inference + checking
│       │   ├── SymbolTable.cs          # Scoped name resolution
│       │   └── TypeResolver.cs         # Resolves .NET types from assemblies
│       ├── IR/
│       │   ├── CulebralIR.cs            # IR node types
│       │   └── Lowering.cs             # AST → IR (desugar, flatten)
│       ├── Emit/
│       │   └── CilEmitter.cs           # IR → .NET assembly via PersistedAssemblyBuilder
│       └── Diagnostics/
│           └── DiagnosticBag.cs        # Error/warning collection + reporting
├── tests/
│   └── Culebral.Compiler.Tests/         # xUnit test project
│       ├── Culebral.Compiler.Tests.csproj
│       ├── LexerTests.cs
│       ├── ParserTests.cs
│       ├── TypeCheckerTests.cs
│       └── EmitTests.cs               # End-to-end: source → assembly → run → assert
├── samples/
│   ├── hello.cbl                       # def main(): print("Hello from Culebral!")
│   ├── fibonacci.cbl
│   └── classes.cbl
├── culebral.sln
└── README.md
```

### First Compilation Target (Hello World)

The first program the compiler must handle:

```python
# hello.cbl
def main():
    print("Hello from Culebral!")
```

The CIL emitter generates the equivalent of:

```csharp
// What PersistedAssemblyBuilder produces
var ab = new PersistedAssemblyBuilder(
    new AssemblyName("hello"), typeof(object).Assembly);
var mob = ab.DefineDynamicModule("hello");
var tb = mob.DefineType("Program",
    TypeAttributes.Public | TypeAttributes.Class);
var main = tb.DefineMethod("Main",
    MethodAttributes.Public | MethodAttributes.Static,
    typeof(void), Type.EmptyTypes);

var il = main.GetILGenerator();
il.Emit(OpCodes.Ldstr, "Hello from Culebral!");
il.Emit(OpCodes.Call,
    typeof(Console).GetMethod("WriteLine", new[] { typeof(string) }));
il.Emit(OpCodes.Ret);

tb.CreateType();
ab.Save("hello.dll");
// Run with: dotnet hello.dll
```

### Build and Run Workflow

```bash
# Scaffold the compiler project (one-time)
dotnet new sln -n culebral
dotnet new console -n Culebral.Compiler -o src/Culebral.Compiler
dotnet new xunit -n Culebral.Compiler.Tests -o tests/Culebral.Compiler.Tests
dotnet sln add src/Culebral.Compiler tests/Culebral.Compiler.Tests
dotnet add tests/Culebral.Compiler.Tests reference src/Culebral.Compiler

# Build the compiler
dotnet build

# Compile a Culebral program
dotnet run --project src/Culebral.Compiler -- build samples/hello.cbl

# Run the output
dotnet samples/hello.dll

# Eventually, once installed as a tool:
culebral build hello.cbl
culebral run hello.cbl
```

### Phase Plan

**Phase 1 — Minimum Viable Language (8-12 weeks)**
- Lexer with INDENT/DEDENT
- Parser for: functions, variables, if/else, for/while, basic expressions
- Type checking for primitives and basic inference
- CulebralIR generation
- CIL emission for simple programs
- `when target` syntax (parsed, only `"net"` supported)
- **Goal:** `def main(): print("hello")` compiles and runs on `dotnet`

**Phase 2 — Type System + Classes (8-12 weeks)**
- Full type inference in function bodies
- Classes with fields, methods, constructors
- Interfaces
- Generics (at least basic)
- **Goal:** Can define and use custom types, implement interfaces

**Phase 3 — .NET Interop (6-8 weeks)**
- Import .NET namespaces and types
- Case convention bridging
- NuGet package resolution
- Extension method support
- **Goal:** Can use HttpClient, LINQ, read JSON — real programs

**Phase 4 — Language Completeness (ongoing)**

Phase 4 is broken into sub-phases ordered by dependency and impact. Features in each batch are independent of later batches but may depend on earlier ones.

**Batch 1 — Core Operators & Statements** ✅ COMPLETE
- `is` / `is not` operators (runtime type checks and null checks)
- `in` / `not in` operators (collection membership)
- `raise` statement (throw exceptions)
- `try`/`except`/`finally` (exception handling)
- `**` (power) and `//` (floor division) operator emission
- `range(start, stop)` and `range(start, stop, step)` overloads
- `__str__` / `__repr__` → `ToString()` mapping
- Pattern matching with algebraic enum dispatch

**Batch 2 — Missing Features** (see §Missing Features below for full specification)
- Lambda expressions → delegate emission
- `with` statement → `IDisposable` / `try-finally`
- Dict comprehensions and set literals/comprehensions
- Generator expressions → `IEnumerable<T>` with `yield`
- Slicing (`items[1:3]`, `items[::-1]`)
- Tuple unpacking / multiple assignment (`a, b = b, a`)
- `*args` unpacking (maps to `params T[]`)
- Record `with` expressions (`alice with (name="Bob")`)
- Type alias (`type HashMap[K, V] = Dictionary[K, V]`)
- Explicit type casts
- Decorator emission (attributes and wrappers)

**Batch 3 — Advanced Type System & Safety**
- Null safety with flow typing
- Generic constraints enforcement
- Named tuple returns and destructuring
- Comparison chaining (`a < b < c`)
- Augmented assignment operators (`<<=`, `>>=`, `**=`, etc.)
- Operator overloading via dunder methods

**Batch 4 — Async Runtime**
- Async/await codegen (Task-based, IAsyncStateMachine)
- `async for` → `IAsyncEnumerable<T>`
- `async with` → `IAsyncDisposable`

**Batch 5 — Tooling & Developer Experience**
- LSP server for editor support
- Source maps / PDB debug information
- REPL (interactive mode)
- `culebral fmt` (formatter)
- `culebral test` (built-in test runner)

**Phase 5 — Standard Library (minimal)**
- `Result` type: built-in (already implemented)
- `culebral.testing`: test runner for `culebral test` (when needed)
- No BCL wrappers needed — .NET interop with case bridging is the standard library

**Phase 6 — Native Modules (LOWEST PRIORITY)**

> ⚠ **This phase is the very last priority.** Everything above must be solid before any native work begins. The architectural decisions (CulebralIR boundary, `when target` syntax, module resolution paths) are already in place to support this — no further work is needed until this phase is actively started.

- LLVM backend emitting shared libraries from `@native` modules
- Auto-generated P/Invoke bridge from .NET side to native modules
- Marshaling layer for types crossing the managed/native boundary
- GC integration (Boehm initially, precise GC later)
- C FFI support for native modules calling external C libraries
- `culebral.std` native implementations behind `when target == "native"` branches
- **Goal:** `@native` module compiles to `.so`/`.dll`, callable from .NET Culebral code seamlessly

---

## Native Modules (Future — Phase 6)

> ⚠ **This entire section describes future work that is the lowest priority item in the project.** It is included here for architectural planning only. Do not implement any of this until Phases 1-5 are complete and stable.

Native modules allow performance-critical code to compile to native binaries via LLVM while the main application remains a .NET project. The managed runtime is the host; native code is called for hot paths.

### Syntax

```python
# app.cbl — compiles to .NET as usual
from engine import process_frame

async def main():
    data = await get("https://api.example.com/frames")
    result = process_frame(data)
    print(result)
```

```python
# engine.cbl — native module
@native
module engine:

    def process_frame(data: bytes) -> bytes:
        # compiles to native .so/.dll via LLVM
        # no GC pressure, SIMD available
        ...
```

### How It Works

1. Compiler sees `@native` annotation on a module
2. `engine.cbl` is compiled through the LLVM backend → shared library (`engine.so` / `engine.dll` / `engine.dylib`)
3. Compiler auto-generates P/Invoke bridge on the .NET side (user never writes `DllImport`)
4. `culebral build` runs both backends and links outputs together

### Boundary Constraints

Native modules enforce a strict boundary for types crossing managed/native:

| Allowed | Not Allowed |
|---|---|
| Primitives (int, float, bool) | Classes |
| `bytes` (byte arrays) | Interfaces |
| Fixed-layout `struct` types | Generics crossing the boundary |
| `str` (marshaled as UTF-8) | Closures / lambdas |

Native modules cannot import .NET types. They can use `culebral.std` native implementations and C FFI bindings only.

### Build System

```toml
# culebral.toml
[project]
name = "my-app"
version = "0.1.0"
target = "net10.0"

[dependencies.net]
"Newtonsoft.Json" = "13.0.3"

[modules.native]
engine = { path = "engine.cbl" }

[dependencies.native]
libcurl = { ffi = true }
```

One `culebral build` compiles everything — .NET modules through CIL, native modules through LLVM, bridge code auto-generated.

---

## Missing Features — Detailed Specification

> This section documents every language feature that is either **not yet implemented** or **partially implemented** in the compiler. Each entry includes the design, the .NET compilation target, performance considerations, security implications where relevant, and implementation notes.
>
> Features are grouped by category. Within each category, entries are ordered by dependency (implement earlier entries first).

---

### 4.1 Lambda Expressions → Delegate Emission

**Status:** Parsed in AST. Lowering emits `null` (stub). Not functional.

**What it does:** Lambda expressions create anonymous functions. Culebral supports both single-expression and multi-line lambdas (Python only supports single-expression).

```python
# Single-expression lambda (like Python)
square = lambda x: x * 2

# Multi-line lambda (Culebral extension — Python lacks this)
transform = lambda x:
    result = x * 2
    return f"Value: {result}"

# Lambdas as arguments
numbers = [3, 1, 4, 1, 5]
sorted_nums = numbers.order_by(lambda x: x)

# Type-annotated lambdas (optional, inferred from context)
typed_fn: Func[int, str] = lambda x: str(x)
```

**Compilation target:** Each lambda compiles to a .NET delegate instance. The compiler must:

1. **Generate a hidden method** on the enclosing class (or a compiler-generated display class if the lambda captures local variables). The method signature matches the inferred or declared delegate type.
2. **Emit a delegate constructor** — `newobj Func<T,R>::.ctor(object, native int)` with `ldftn` or `ldvirtftn` pointing to the generated method.
3. **Handle closures** — If the lambda references variables from the enclosing scope, generate a display class (`<>c__DisplayClass`) with fields for each captured variable. The enclosing method allocates the display class and stores captured values into it. The lambda method becomes an instance method on the display class.

**Closure capture rules:**
- Capture by reference (like C#), not by value. Mutations inside the lambda are visible outside and vice versa.
- The display class must be allocated once per enclosing scope activation, not once per lambda.
- Multiple lambdas in the same scope that capture the same variable share one display class instance.

**Performance considerations:**
- Non-capturing lambdas should be cached as static fields (singleton delegate) to avoid allocation on every call. This is critical for hot paths like LINQ chains.
- The compiler should detect pure (non-capturing) lambdas and emit `static` methods + cached delegate fields. C# does this and it eliminates GC pressure in tight loops.
- For `Action`/`Func` delegates with ≤16 type parameters, use the BCL generic delegate types. Do not generate custom delegate types unless necessary (e.g., `ref` params, `out` params).

**Security considerations:**
- Closures that capture mutable state create shared mutable references. The compiler should not introduce additional unsafety, but users should understand that captured variables are shared, not copied.

**Implementation notes:**
- The IR needs a new instruction: `IrCreateDelegate(targetMethod, capturedLocals[], delegateType)`.
- The emitter must handle `Delegate.CreateDelegate` or the `ldftn` + `newobj` pattern depending on whether the target is static or instance.
- Multi-line lambdas parse as `LambdaExpr` with a `Block` body. Single-expression lambdas parse with a single expression body. Both lower to the same generated method — the distinction is purely syntactic.

---

### 4.2 `with` Statement → IDisposable / try-finally

**Status:** Parsed in AST. Lowering skips it (emits warning CBL3001). Not functional.

**What it does:** The `with` statement ensures deterministic resource cleanup by calling `Dispose()` when the block exits, regardless of exceptions.

```python
# Single resource
with open("data.txt") as f:
    content = f.read()
# f.Dispose() called here, even if an exception occurs

# Multiple resources (left to right allocation, right to left disposal)
with SqlConnection(conn_str) as conn, conn.begin_transaction() as tx:
    conn.execute("INSERT INTO users VALUES (...)")
    tx.commit()
# tx.Dispose() called first, then conn.Dispose()

# Without binding (just for side effects)
with acquire_lock(resource):
    do_critical_work()
```

**Compilation target:** Each `with` item compiles to a `try-finally` block:

```
# For: with EXPR as NAME:
local = EXPR
try:
    NAME = local
    <body>
finally:
    if local is not None:
        local.Dispose()
```

For multiple `with` items, they nest — the first item is the outermost `try-finally`, the last is the innermost. This means resources are disposed in reverse order of acquisition (LIFO), which is the correct behavior for dependent resources.

**CIL emission pattern:**
```
// with EXPR as name:
evaluate EXPR
stloc temp
.try {
    ldloc temp
    stloc name
    <body instructions>
    leave end_with
}
finally {
    ldloc temp
    brfalse skip_dispose
    ldloc temp
    callvirt System.IDisposable::Dispose()
    skip_dispose:
    endfinally
}
end_with:
```

**Performance considerations:**
- The `null` check before `Dispose()` is required because the expression might return `null` (nullable types). For non-nullable types where the compiler can prove the value is never null, the check can be elided.
- The `finally` block should use `callvirt` on `IDisposable.Dispose()`, not a direct method call, to support derived types that override `Dispose`.
- For struct types implementing `IDisposable`, the compiler should use `constrained. callvirt` to avoid boxing. This is a significant performance win for types like `SpinLock`, `CancellationTokenRegistration`, etc.

**Security considerations:**
- Resource cleanup is critical for security-sensitive resources (file handles, network connections, cryptographic key material). The `finally` block guarantees cleanup even on exception paths. This is non-negotiable — a missed `Dispose` on a crypto stream can leak key material.
- The compiler must ensure that the `finally` block is emitted for every code path, including early `return`, `break`, and `continue` inside the `with` body.

**Implementation notes:**
- The lowering pass should desugar `WithStatement` into `TryStatement` with a synthetic `finally` block before IR generation. This avoids adding new IR instructions — reuses the existing exception handling infrastructure.
- The type checker must verify that the expression type implements `System.IDisposable`. If it doesn't, emit a compile error: "Type 'X' cannot be used in a 'with' statement because it does not implement IDisposable."
- `__enter__` / `__exit__` protocol (Python) is NOT used. Culebral maps directly to .NET's `IDisposable` pattern. This is simpler, faster, and interops perfectly with the entire .NET ecosystem.

---

### 4.3 Dict Comprehensions

**Status:** Parsed in AST (`DictComprehension`). Lowering falls through to default case (emits `null`). Not functional.

**What it does:** Dict comprehensions construct dictionaries from an iterable with an optional filter.

```python
# Basic dict comprehension
word_lengths = {word: len(word) for word in words}

# With filter
long_words = {w: len(w) for w in words if len(w) > 5}

# With expression transforms
squares = {x: x ** 2 for x in range(10)}
```

**Compilation target:** Compiles to `Dictionary<TKey, TValue>` construction with a loop:

```
// {k_expr: v_expr for var in iterable if condition}
new Dictionary<TKey, TValue>()
stloc dict
// for loop over iterable:
    // if condition:
        evaluate k_expr
        evaluate v_expr
        ldloc dict
        call Dictionary.Add(key, value)
ldloc dict
```

**Performance considerations:**
- If the source iterable has a known count (e.g., `range(n)`, a list, an array), pass the count as the `capacity` argument to the `Dictionary` constructor. This eliminates rehashing during construction. For a dictionary built from 1000 items, this avoids ~10 internal resize+rehash operations.
- For value-type keys and values, the compiler should emit `Dictionary<int, int>` (or the appropriate concrete types), not `Dictionary<object, object>`. This avoids boxing overhead — each boxed int is a 24-byte heap allocation vs. 4 bytes inline.
- The type checker should infer the key and value types from the expressions, and the IR should carry these concrete types so the emitter can construct the properly-typed dictionary.

**Implementation notes:**
- Reuse the same lowering pattern as `ListComprehension` (which is already implemented). The structure is identical — allocate container, loop, conditionally add, return container.
- Needs `IrNewObj` for `Dictionary<K,V>()`, `IrCallVirtual` for `Dictionary.Add(K,V)`.

---

### 4.4 Set Literals and Set Comprehensions

**Status:** `SetExpr` and set comprehensions parsed in AST. Lowering falls through to default case (emits `null`). Not functional.

**What it does:** Set literals and comprehensions construct `HashSet<T>` instances.

```python
# Set literal
unique = {1, 2, 3, 4, 5}

# Set comprehension
even_squares = {x ** 2 for x in range(20) if x % 2 == 0}

# Empty set (cannot use {} — that's an empty dict)
empty: set[int] = set()
```

**Compilation target:** Maps to `System.Collections.Generic.HashSet<T>`.

```
// {expr for var in iterable if condition}
new HashSet<T>()  // or new HashSet<T>(capacity) if count is known
stloc result
// for loop:
    // if condition:
        evaluate expr
        ldloc result
        call HashSet<T>.Add(T)
ldloc result
```

**Disambiguation rule:** `{}` is always an empty dict (like Python). To create an empty set, use `set()` or a type-annotated assignment `x: set[int] = set()`. This matches Python's behavior.

**Performance considerations:**
- Same as dict comprehensions — pass known capacity to avoid rehashing.
- `HashSet<T>` for value types avoids boxing. The type checker must propagate the element type correctly.
- `HashSet<T>.Add()` returns `bool` (whether the element was new). The compiler must pop this value from the evaluation stack since it's unused in a comprehension context.

---

### 4.5 Generator Expressions → IEnumerable\<T> with yield

**Status:** Parsed in AST (`GeneratorExpr`). Lowering falls through to default case (emits `null`). Not functional.

**What it does:** Generator expressions produce lazy sequences. Elements are computed on demand, not materialized in memory.

```python
# Generator expression — lazy, evaluates one at a time
evens = (x for x in range(1_000_000) if x % 2 == 0)

# Used directly in function calls (no extra parens needed)
total = sum(x ** 2 for x in range(100))

# Chained with LINQ
result = (x * 2 for x in data).where(lambda x: x > 10).to_list()
```

**Compilation target:** Generators compile to a state machine class that implements `IEnumerable<T>` and `IEnumerator<T>`. This is the same pattern C# uses for `yield return`.

The compiler must generate:

1. **A state machine class** with fields for: the current state (int), the current yielded value, and all captured variables from the enclosing scope.
2. **`MoveNext()` method** containing a switch on the state field. Each `yield` point is a state transition. Between yields, the method executes the loop body, evaluates the filter, and computes the element expression.
3. **`Current` property** returning the last yielded value.
4. **`GetEnumerator()` method** returning `this` (for single-use) or a clone (for multi-use).
5. **`Dispose()` method** for cleanup of the enumerator.

**State machine structure (conceptual):**
```csharp
class <GenExpr>d__0 : IEnumerable<T>, IEnumerator<T> {
    int state;
    T current;
    // captured variables as fields

    bool MoveNext() {
        switch (state) {
            case 0:
                // initialize iterator
                state = 1;
                goto case 1;
            case 1:
                // advance to next matching element
                while (iterator.MoveNext()) {
                    var x = iterator.Current;
                    if (condition(x)) {
                        current = transform(x);
                        state = 1;
                        return true;
                    }
                }
                return false;
        }
    }
}
```

**Performance considerations:**
- Generators avoid materializing the entire sequence in memory. For `(x for x in range(1_000_000))`, only one element exists at a time vs. a million-element list.
- The state machine class should be a `struct` when possible (non-capturing, single iteration) to avoid heap allocation. C# does this optimization for `async` methods and it eliminates GC pressure entirely.
- The compiler should detect when a generator is immediately consumed by a single call (e.g., `sum(x for x in ...)`) and consider inlining the generation logic directly into the consuming loop, eliminating the state machine overhead entirely.

**Implementation notes:**
- This is one of the more complex features. The `YieldStatement` lowering is also a prerequisite — both generators and generator expressions need the same state machine infrastructure.
- Consider implementing `YieldStatement` first (in standalone generator functions), then reuse the infrastructure for generator expressions.
- The IR needs a `yield` instruction that marks a suspension point. The emitter transforms functions containing `yield` into state machine classes.

---

### 4.6 Yield Statement (Generator Functions)

**Status:** Parsed in AST (`YieldStatement`). Lowering skips it (emits warning CBL3001). Not functional.

**What it does:** `yield` turns a function into a generator — a lazy sequence producer.

```python
def fibonacci() -> IEnumerable[int]:
    a = 0
    b = 1
    while True:
        yield a
        a, b = b, a + b

# Usage
for n in fibonacci():
    if n > 1000:
        break
    print(n)
```

**Compilation target:** Same state machine pattern as generator expressions (see §4.5). A function containing `yield` is rewritten into a class implementing `IEnumerable<T>` / `IEnumerator<T>`.

**Key difference from generator expressions:** Generator functions can have multiple `yield` points, `yield` inside loops, `yield` inside conditionals, and `yield` inside try blocks (with restrictions). Each `yield` point is a distinct state in the state machine.

**`yield from` (delegation):**
```python
def flatten(nested: list[list[int]]) -> IEnumerable[int]:
    for inner in nested:
        yield from inner
```

`yield from` delegates to another iterable. It compiles to a loop that yields each element from the inner iterable. This is syntactic sugar for `for item in inner: yield item` but communicates intent more clearly.

**Performance considerations:**
- Generator functions that never escape the local scope (e.g., immediately consumed in a `for` loop) should be candidates for inlining. The state machine overhead (~40 bytes for the class + vtable dispatch on each `MoveNext`) adds up in tight loops.
- For generators that produce value types, ensure the `Current` property returns `T` directly (not `object`) to avoid boxing.

---

### 4.7 Slicing

**Status:** Parsed in AST (`SliceExpr` with `Lower`, `Upper`, `Step` fields). Lowering falls through to default case (emits `null`). Type checker returns `object` with a TODO comment. Not functional.

**What it does:** Slicing extracts sub-sequences from lists, strings, and other indexable types.

```python
items = [10, 20, 30, 40, 50]

# Basic slice
first_three = items[0:3]        # [10, 20, 30]
last_two = items[-2:]           # [40, 50]
middle = items[1:4]             # [20, 30, 40]

# With step
every_other = items[::2]        # [10, 30, 50]
reversed_list = items[::-1]     # [50, 40, 30, 20, 10]

# String slicing
name = "Culebral"
first_four = name[:4]           # "Skel"
backwards = name[::-1]          # "novlekS"

# Slice assignment (mutable sequences only)
items[1:3] = [200, 300]         # items is now [10, 200, 300, 40, 50]
```

**Compilation target:** Slicing compiles differently based on the receiver type:

**For `List<T>` / arrays:**
- `items[a:b]` → `items.GetRange(a, b - a)` (or `new List<T>(items.Skip(a).Take(b - a))` for arrays)
- `items[::step]` → runtime helper that iterates with step
- `items[::-1]` → `new List<T>(items); result.Reverse()` or `items.AsEnumerable().Reverse().ToList()`
- Negative indices: `items[-n]` → `items[items.Count - n]`, resolved at runtime

**For `string`:**
- `s[a:b]` → `s.Substring(a, b - a)` (or `s.AsSpan(a, b - a).ToString()` for zero-alloc intermediate)
- `s[::-1]` → `new string(s.Reverse().ToArray())` or `string.Create(s.Length, s, ...)`

**Runtime helper approach:** The cleanest strategy is a set of compiler-generated static helper methods:

```csharp
// Generated once per assembly
static class SliceHelper {
    static List<T> Slice<T>(List<T> source, int? start, int? stop, int? step) { ... }
    static string Slice(string source, int? start, int? stop, int? step) { ... }
    static T[] Slice<T>(T[] source, int? start, int? stop, int? step) { ... }
}
```

This centralizes the index normalization logic (negative indices, `None` defaults, step direction) in one place.

**Performance considerations:**
- `items[0:n]` with known-positive indices should compile directly to `GetRange` / `Substring` without going through the general-purpose helper. This avoids nullable boxing and the step logic.
- `s[a:b]` on strings should use `Span<char>` when the result is immediately consumed (e.g., passed to another function) to avoid allocating a new string. This requires escape analysis — if the slice doesn't escape, use `ReadOnlySpan<char>`.
- Negative index resolution (`items[-1]`) is a single subtraction at runtime. The compiler should emit `ldlen` + `ldc.i4 n` + `sub` inline rather than calling a helper for this common case.
- Step-based slicing (`items[::2]`) allocates a new list. For read-only consumption, consider emitting an `IEnumerable<T>` that yields elements at the step interval (lazy).

**Security considerations:**
- Index bounds must be clamped, not unchecked. Python silently clamps out-of-range slice indices (e.g., `[0:9999]` on a 5-element list returns all 5 elements). Culebral must match this behavior. Out-of-bounds slicing must never throw — it clamps to valid range.
- This is different from single-element indexing (`items[9999]`), which should throw `IndexOutOfRangeException`.

**Implementation notes:**
- The IR needs an `IrSlice(hasStart, hasStop, hasStep)` instruction that operates on the stack (receiver, then optional start/stop/step values).
- The type checker must infer the result type: slicing a `list[T]` returns `list[T]`, slicing a `str` returns `str`.
- Slice assignment (`items[1:3] = [200, 300]`) is a separate feature that requires mutation support. Implement read-only slicing first.

---

### 4.8 Tuple Unpacking / Multiple Assignment

**Status:** `TupleExpr` is parsed as an expression. Assignment targets only handle `IdentifierExpr`, `FieldAccessExpr`, and `MemberAccessExpr` — no support for `TupleExpr` as an assignment target. Not functional for unpacking.

**What it does:** Destructures a tuple (or any iterable) into multiple variables in a single assignment.

```python
# Basic swap
a, b = b, a

# Unpacking from function return
quotient, remainder = divmod(17, 5)

# Nested unpacking
(a, b), c = (1, 2), 3

# Star unpacking (rest capture)
first, *middle, last = [1, 2, 3, 4, 5]
# first = 1, middle = [2, 3, 4], last = 5

# Unpacking in for loops
pairs = [(1, "a"), (2, "b"), (3, "c")]
for num, letter in pairs:
    print(f"{num}: {letter}")
```

**Compilation target:**

**Simple case (`a, b = expr`):**
```
evaluate RHS (yields tuple/iterable)
stloc temp
ldloc temp
call ValueTuple.Item1  // or get_Item1 for named tuples
stloc a
ldloc temp
call ValueTuple.Item2
stloc b
```

**Swap case (`a, b = b, a`):**
The compiler must evaluate ALL RHS expressions before ANY assignment occurs. This is critical — `a, b = b, a` must work correctly as a swap without a temp variable visible to the user.
```
ldloc b     // evaluate RHS first
ldloc a
stloc temp_b
stloc temp_a
ldloc temp_a
stloc a
ldloc temp_b
stloc b
```

**Star unpacking (`first, *rest, last = iterable`):**
```
evaluate iterable
stloc source
// first = source[0]
// last = source[source.Count - 1]
// rest = source.GetRange(1, source.Count - 2)
```

**Performance considerations:**
- For `ValueTuple` unpacking, the JIT inlines the field access — there is zero overhead vs. manual `var a = tuple.Item1; var b = tuple.Item2;`.
- Star unpacking requires allocating a `List<T>` for the rest elements. For known-size sources, the compiler can pre-allocate with the correct capacity.
- For the swap pattern specifically, the compiler should detect `a, b = b, a` and emit minimal stack manipulation (two loads, two stores) without any temp locals or tuple construction.

**Implementation notes:**
- The assignment handler in `Lowering.cs` needs a new case for `TupleExpr` as the assignment target.
- For unpacking, the type checker needs to verify that the RHS type is a tuple or iterable with enough elements. If the RHS is `(int, str)` and the LHS has 3 targets, it's a compile error.
- Star targets (`*rest`) require counting the non-star targets, then allocating the rest as a list of the remaining elements.

---

### 4.9 `*args` Unpacking (Variadic Parameters)

**Status:** `Parameter` AST node has a `IsVarArgs` flag. Parser handles `*args` syntax. Lowering does not handle `params` array generation. Not functional.

**What it does:** Allows functions to accept a variable number of positional arguments, collected into a typed array.

```python
def log(level: str, *messages: str):
    for msg in messages:
        print(f"[{level}] {msg}")

log("INFO", "Starting", "Processing", "Done")
# messages is ["Starting", "Processing", "Done"]

# Unpacking at call site
args = ["Starting", "Processing", "Done"]
log("INFO", *args)
```

**Compilation target:** Maps to C#'s `params` keyword.

```csharp
// def log(level: str, *messages: str) compiles to:
static void Log(string level, params string[] messages) { ... }
```

**Call site compilation:**
- `log("INFO", "a", "b")` → compiler creates `new string[] {"a", "b"}` and passes it
- `log("INFO", *args)` → if `args` is already `string[]`, pass directly; otherwise `args.ToArray()`

**Performance considerations:**
- `params` array allocation happens on every call. For hot paths, consider detecting when the callee immediately iterates the array and inlining the arguments directly (loop unrolling).
- .NET 10 has `params ReadOnlySpan<T>` support which avoids the array allocation entirely. The compiler should emit `params ReadOnlySpan<T>` when targeting .NET 10+ and the function only reads the args (doesn't store or return them). This is a significant win — zero allocation for variadic calls.
- The `*args` syntax at the call site should pass the array directly when types match, avoiding a copy.

**Implementation notes:**
- The IR needs to support creating arrays from N stack values: `IrNewArrayFromStack(elementType, count)`.
- The type checker must enforce that `*args` is the last positional parameter (no positional params after it).
- Type inference: `*messages: str` means `messages` has type `str[]` inside the function body.

---

### 4.10 Record `with` Expressions

**Status:** Parsed in AST (`WithExpr`). Lowering falls through to default case (emits `null`). Not functional.

**What it does:** Creates a modified copy of an immutable record, changing specified fields while preserving all others.

```python
record User:
    name: str
    email: str
    age: int

alice = User("Alice", "alice@example.com", 30)
bob = alice with (name="Bob", email="bob@example.com")
# bob is User("Bob", "bob@example.com", 30) — age preserved from alice
```

**Compilation target:** Records compile to C# `record` types which already support `with` expressions natively. The compiler emits:

```
// bob = alice with (name="Bob", email="bob@example.com")
ldloc alice
// The CLR's record with-expression support generates a clone method:
call User.<Clone>$()
stloc temp
ldloc temp
ldstr "Bob"
stfld User::name
ldloc temp
ldstr "bob@example.com"
stfld User::email
ldloc temp
stloc bob
```

Alternatively, the compiler can emit a constructor call with the modified fields:
```
// More efficient — single allocation, no mutation
ldstr "Bob"                      // name (overridden)
ldstr "bob@example.com"          // email (overridden)
ldloc alice
ldfld User::age                  // age (preserved from original)
newobj User::.ctor(string, string, int)
stloc bob
```

**Performance considerations:**
- The constructor approach (option 2) is preferred — it's a single allocation with no intermediate mutation. The clone-then-mutate approach allocates one object and then mutates it, which is equivalent in allocation but requires an extra copy.
- For records with many fields where only one is changed, the constructor approach still copies all fields. This is optimal — the alternative (cloning and mutating) isn't faster and is harder to verify for correctness.
- Records should have structural equality (auto-generated `Equals` and `GetHashCode`). The compiler should generate these methods based on all fields, with value-type aware comparisons.

**Implementation notes:**
- The IR needs an instruction like `IrRecordWith(recordType, fieldNames[], fieldCount)` that pops the new field values and the original record from the stack.
- The emitter should use the constructor approach, not the clone approach, for deterministic initialization.
- The type checker must verify that all field names in the `with` expression exist on the record type, and that the value types are assignment-compatible.

---

### 4.11 Type Alias

**Status:** `type` keyword is lexed. No AST node, parser rule, or lowering exists for type alias declarations. Not functional.

**What it does:** Creates an alternative name for an existing type, improving readability without introducing a new type.

```python
# Simple alias
type UserId = int
type Email = str

# Generic alias
type HashMap[K, V] = Dictionary[K, V]
type StringMap[V] = Dictionary[str, V]

# Complex alias
type Callback = Func[int, str, bool]
type EventHandler = Action[object, EventArgs]
type Result[T] = Result[T, Exception]

# Usage — aliases are interchangeable with the original type
def find_user(id: UserId) -> User?:
    return database.get(id)

users: HashMap[int, User] = HashMap()
```

**Compilation target:** Type aliases are a compile-time concept. They generate NO runtime code. The compiler simply substitutes the alias with the underlying type everywhere it appears.

- `type UserId = int` → everywhere `UserId` appears in signatures, the compiler emits `int32`
- `type HashMap[K, V] = Dictionary[K, V]` → `HashMap[str, int]` resolves to `Dictionary<string, int>`

**Performance considerations:**
- Zero runtime overhead. Type aliases are erased during compilation. They're purely a readability tool.
- Generic aliases must be expanded at each usage site with concrete type arguments. `StringMap[User]` becomes `Dictionary<string, User>` before any IR is generated.

**Implementation notes:**
- Add `TypeAliasStatement` to the AST: `type Name[TypeParams] = TypeAnnotation`.
- The type checker registers aliases in the symbol table during Pass 1 (declaration collection). During type resolution, aliases are expanded recursively until a concrete type is reached.
- Cyclic aliases must be detected and rejected: `type A = B` + `type B = A` → compile error.
- The parser needs a new rule: when `type` keyword is seen at statement level, parse `type NAME [GENERIC_PARAMS] = TYPE_ANNOTATION`.

---

### 4.12 Explicit Type Casts

**Status:** Parsed in AST (`TypeCastExpr`). Lowering falls through to default case (emits `null`). Not functional.

**What it does:** Explicitly converts a value from one type to another.

```python
# Numeric casts
x: float = 3.14
n = int(x)           # Truncates to 3

# Upcasting (always safe)
obj: object = "hello"

# Downcasting (checked at runtime)
s = str(obj)         # Succeeds — obj is a string

# Using 'as' for safe downcasting (returns nullable)
maybe_str = obj as str  # Returns str? — None if cast fails
```

**Compilation target:**

| Cast type | CIL instruction | Behavior |
|---|---|---|
| Upcast (derived → base) | No instruction needed | Always safe, implicit |
| Downcast (base → derived) | `castclass` | Throws `InvalidCastException` on failure |
| Safe downcast (`as`) | `isinst` + null check | Returns null on failure |
| Numeric widening (int → float) | `conv.r8` | Lossless |
| Numeric narrowing (float → int) | `conv.i4` | Truncates (rounds toward zero) |
| Unbox value type | `unbox.any` | Throws if wrong type |

**Performance considerations:**
- `castclass` and `isinst` are relatively fast on modern .NET (JIT-optimized type checks). Don't avoid casts for performance — use them when the type system requires it.
- For numeric conversions, the `conv.*` instructions are single-cycle operations on modern hardware.
- Avoid chains of casts. If you find yourself casting through multiple types, the type system design may need revisiting.

**Security considerations:**
- Downcasts are runtime-checked. A `castclass` that fails throws `InvalidCastException` with the source and target type names in the message. In sensitive contexts, catching this exception and re-throwing with a generic message prevents type information leakage.
- The `as` pattern (try-cast returning nullable) should be preferred over hard casts when the target type is uncertain, to avoid exception-driven control flow.

---

### 4.13 Decorator Emission

**Status:** Parsed in AST (decorators stored on `FunctionDef` and `ClassDef`). Lowering completely ignores decorators. Not functional (except `@native` which is checked by name only).

**What it does:** Decorators modify the behavior of functions or classes at compile time. They map to either .NET attributes or wrapper functions.

```python
# Attribute decorator — maps to .NET attribute
@obsolete("Use new_method instead")
def old_method():
    pass

# Wrapper decorator — generates a wrapping function
@retry(max_attempts=3, delay=1.0)
def fetch_data(url: str) -> str:
    return http_get(url)

# Class decorator
@serializable
class Config:
    host: str
    port: int

# ASP.NET route decorator (maps to attribute)
@route("/api/users/{id}")
@authorize("admin")
async def get_user(id: int) -> User:
    return await user_service.find(id)
```

**Compilation target:** Two strategies based on the decorator type:

**1. Attribute decorators** (when the decorator resolves to a .NET attribute type):
```
// @obsolete("message") on a method →
.method public static void OldMethod() {
    .custom instance void [System.Runtime]System.ObsoleteAttribute::.ctor(string) = { "Use new_method instead" }
    // method body
}
```

**2. Wrapper decorators** (when the decorator is a function that returns a function):
```python
# This:
@retry(max_attempts=3)
def fetch(url: str) -> str: ...

# Compiles as if the user wrote:
def _fetch_inner(url: str) -> str: ...
fetch = retry(max_attempts=3)(_fetch_inner)
```

The compiler generates the inner function under a mangled name, then emits the decorator call at the module initialization level.

**Performance considerations:**
- Attribute decorators have zero runtime overhead — they're metadata only, queried via reflection when needed.
- Wrapper decorators add one level of indirection per decorator. For hot paths, the JIT will typically inline the wrapper if it's small enough. Stacking multiple wrapper decorators (3+) on a single function adds measurable overhead.
- The compiler should detect "identity" decorators (decorators that return the original function unchanged, like logging decorators) and optimize away the wrapper when the decorator body has no side effects.

**Implementation notes:**
- During type checking, resolve each decorator name. If it resolves to a type inheriting from `System.Attribute`, use the attribute strategy. If it resolves to a callable, use the wrapper strategy.
- Decorator arguments must be compile-time constants for attribute decorators (this is a .NET requirement). For wrapper decorators, arguments can be any expression.
- Decorator order matters: `@A @B def f()` means `f = A(B(f))`. Decorators are applied bottom-up.

---

### 4.14 Null Safety with Flow Typing

**Status:** `NullableType` is parsed (`T?`). `is None` / `is not None` checks work at runtime. The type checker does NOT narrow types after null checks — no flow typing. Partially functional.

**What it does:** The compiler tracks nullability through control flow, narrowing nullable types to their non-nullable counterparts after null checks.

```python
def process(user: User?) -> str:
    # Here, user is User? — could be None
    if user is None:
        return "No user"

    # Here, the compiler KNOWS user is User (not None)
    # because the None case already returned
    return user.name  # No error — type narrowed to User

def find_and_greet(id: int) -> str:
    user = find_user(id)  # Returns User?

    # Guard clause narrows the type
    if user is None:
        raise ValueError(f"User {id} not found")

    # user is User here
    return f"Hello, {user.name}!"
```

**How flow typing works:**

The type checker maintains a "type state" that is modified at branch points:

1. **After `if x is None: return`** — in the continuation, `x` is narrowed from `T?` to `T`.
2. **After `if x is not None:`** — inside the if body, `x` is narrowed to `T`. In the else body (if present), `x` is `None`.
3. **After `if x is SomeType:`** — inside the body, `x` is narrowed to `SomeType`.
4. **After `match x: case None: ...; case User(u): ...`** — each arm has appropriate narrowing.

**Type narrowing rules:**

| Check | Inside true branch | After early return from true branch |
|---|---|---|
| `x is None` | `x` is `None` | `x` is `T` (non-nullable) |
| `x is not None` | `x` is `T` | `x` is `None` (dead code) |
| `x is SomeType` | `x` is `SomeType` | unchanged |

**Compiler enforcement:**
```python
def bad(user: User?):
    print(user.name)  # COMPILE ERROR: 'user' might be None

def good(user: User?):
    if user is not None:
        print(user.name)  # OK — narrowed to User
```

**Performance considerations:**
- Flow typing is purely a compile-time analysis. It generates zero runtime overhead — the same code is emitted regardless of whether the type checker does narrowing. The benefit is catching null reference bugs at compile time instead of runtime.
- This eliminates the need for redundant null checks that would otherwise be inserted "just in case." Fewer runtime checks = fewer branches = better branch prediction.

**Security considerations:**
- Null dereference is the #1 cause of runtime crashes in managed languages. Flow typing catches these at compile time. Every null dereference prevented at compile time is a crash prevented in production.
- Nullable types make null handling explicit in function signatures. A function returning `User?` clearly communicates that the caller must handle the absence case. A function returning `User` guarantees a non-null value.

**Implementation notes:**
- The type checker needs a `NarrowingContext` that is forked at branch points and joined at merge points.
- At a branch (`if x is None`), create two narrowing contexts: one where `x` is `None`, one where `x` is `T`. Type-check each branch with its respective context.
- At a join point (after `if-else`), merge the two contexts. If both branches agree on a narrowed type, keep it. If they disagree, widen back to the original type.
- Early returns (`return`, `raise`, `break`) in a branch mean the other branch's narrowing applies to the continuation. This is the most common pattern: `if x is None: return; x.method()`.

---

### 4.15 Generic Constraints Enforcement

**Status:** `TypeParameter` AST node has a `Constraint` field. Parser handles `T: Comparable` syntax. Type checker registers type parameters but does not enforce constraints. Partially functional.

**What it does:** Generic constraints restrict what types can be used as type arguments, enabling the compiler to verify that operations on generic values are valid.

```python
# Interface constraint
def sort[T: Comparable](items: list[T]) -> list[T]:
    # The compiler knows T has comparison operators
    # because Comparable requires them
    ...

# Multiple constraints (intersection)
def serialize[T: Serializable & Printable](item: T) -> str:
    ...

# Base class constraint
class Repository[T: Entity]:
    def save(item: T):
        # Can access Entity's .id field because T: Entity
        db.upsert(item.id, item)

# Constructor constraint (new)
def create_default[T: new]() -> T:
    return T()  # Allowed because T has a parameterless constructor

# Value type constraint (struct)
def swap[T: struct](a: T, b: T) -> (T, T):
    return (b, a)
```

**Compilation target:** Generic constraints map directly to .NET generic constraints:

| Culebral | .NET CIL | Meaning |
|---|---|---|
| `T: SomeInterface` | `where T : SomeInterface` | Must implement interface |
| `T: BaseClass` | `where T : BaseClass` | Must inherit from class |
| `T: struct` | `where T : struct` | Must be a value type |
| `T: class` | `where T : class` | Must be a reference type |
| `T: new` | `where T : new()` | Must have parameterless constructor |
| `T: A & B` | `where T : A, B` | Must satisfy all constraints |

**Performance considerations:**
- Constraints enable the JIT to generate more efficient code. A `T: struct` constraint tells the JIT that `T` is a value type, enabling stack allocation and avoiding boxing.
- Constraints on `IComparable<T>` enable the JIT to devirtualize comparison calls when the concrete type is known at JIT time.

**Implementation notes:**
- The type checker must verify constraints at every generic instantiation site. When a user writes `sort[int](items)`, the checker must verify that `int` implements `Comparable`.
- Constraint checking must be transitive. If `T: U` and `U: Comparable`, then `T` satisfies `Comparable`.
- The emitter must set `GenericParameterAttributes` on `GenericTypeParameterBuilder` to encode constraints in the output assembly metadata.

---

### 4.16 Named Tuple Returns and Destructuring

**Status:** `TupleType` with named elements is parsed. No lowering for named tuple construction or field access. Not functional.

**What it does:** Functions can return multiple named values as a tuple, and callers can destructure them by position or access them by name.

```python
def get_user_info(id: int) -> (name: str, age: int, active: bool):
    return (name="Alice", age=30, active=True)

# Destructure by position
name, age, active = get_user_info(1)

# Access by name
info = get_user_info(1)
print(info.name)    # "Alice"
print(info.age)     # 30
```

**Compilation target:** Named tuples map to `System.ValueTuple<T1, T2, ...>` with `[TupleElementNames]` attribute for preserving names in metadata.

```csharp
// Return type: ValueTuple<string, int, bool> with [TupleElementNames("name", "age", "active")]
// Named access: info.name → info.Item1 (resolved at compile time)
```

**Performance considerations:**
- `ValueTuple` is a value type — stack allocated, no heap allocation for the return value. This is why tuples are preferable to returning a custom class for small grouped values.
- Named access is resolved at compile time (`.name` → `.Item1`). Zero runtime overhead for names.
- For tuples with more than 7 elements, .NET nests `ValueTuple` types: `ValueTuple<T1,...,T7, ValueTuple<T8,...>>`. The compiler must handle this nesting transparently.

---

### 4.17 Comparison Chaining

**Status:** Not parsed. The parser handles binary comparisons but does not recognize chained comparisons. Not implemented.

**What it does:** Multiple comparisons can be chained, and the compiler evaluates them as a conjunction with each intermediate value evaluated only once.

```python
# Chained — each operand evaluated once
if 0 <= x < 100:
    print("in range")

# Equivalent to (but more efficient than):
if 0 <= x and x < 100:
    print("in range")

# Longer chains
if a < b < c < d:
    print("strictly ascending")

# Mixed operators
if 0 <= index < len(items):
    print("valid index")
```

**Compilation target:**

`a < b < c` compiles to:
```
evaluate a
evaluate b
dup                    // keep b for second comparison
stloc temp_b
compare a < b
brfalse short_circuit  // if first comparison fails, skip rest
ldloc temp_b
evaluate c
compare b < c
br end
short_circuit:
    ldc.i4.0           // false
end:
```

The key insight: each intermediate operand is evaluated exactly once and reused for both adjacent comparisons. This is critical for expressions with side effects: `a < f() < c` must call `f()` only once.

**Performance considerations:**
- Short-circuit evaluation: if `a < b` is false, `c` is never evaluated. This matches Python's behavior and is a correctness requirement, not just an optimization.
- The `dup` + `stloc` pattern for intermediate values adds one local variable per chain link. This is negligible — the JIT will register-allocate these.

**Implementation notes:**
- The parser must recognize a sequence of comparison operators and build a `ChainedComparisonExpr` AST node (or transform into a `BinaryExpr` tree with `and` nodes and shared sub-expressions).
- The simpler approach: desugar `a < b < c` into `a < b and b < c` during parsing, but mark `b` as "already evaluated" to avoid double evaluation. This requires a `TempExpr` node or similar mechanism.

---

### 4.18 Augmented Assignment Operators (Extended Set)

**Status:** Common operators (`+=`, `-=`, `*=`, `/=`, `%=`) are implemented. Several operators are lexed but not fully lowered. Partially functional.

**What it does:** The full set of augmented assignment operators:

```python
x += 1      # Addition          ✅ Implemented
x -= 1      # Subtraction       ✅ Implemented
x *= 2      # Multiplication    ✅ Implemented
x /= 2      # Division          ✅ Implemented
x %= 3      # Modulo            ✅ Implemented
x //= 2     # Floor division    ✅ Implemented
x **= 2     # Power             ✅ Implemented
x &= mask   # Bitwise AND       Needs verification
x |= flag   # Bitwise OR        Needs verification
x ^= bits   # Bitwise XOR       Needs verification
x <<= n     # Left shift        Needs verification
x >>= n     # Right shift       Needs verification
```

**Implementation notes:**
- All operator tokens are already lexed. Verify that each augmented assignment operator correctly lowers to a load-operate-store sequence in the IR.
- Bitwise augmented assignments are critical for systems programming (flags, masks, protocol parsing). Test each one explicitly.

---

### 4.19 Operator Overloading via Dunder Methods

**Status:** `__str__` → `ToString()` mapping is implemented. No other dunder method mappings exist. Mostly not implemented.

**What it does:** User-defined classes can override operators by implementing special methods (dunder methods), which map to .NET operator overloads or interface implementations.

```python
class Vector:
    x: float
    y: float

    def __init__(x: float, y: float):
        @x = x
        @y = y

    def __add__(other: Vector) -> Vector:
        return Vector(x + other.x, y + other.y)

    def __sub__(other: Vector) -> Vector:
        return Vector(x - other.x, y - other.y)

    def __mul__(scalar: float) -> Vector:
        return Vector(x * scalar, y * scalar)

    def __eq__(other: Vector) -> bool:
        return x == other.x and y == other.y

    def __lt__(other: Vector) -> bool:
        return magnitude() < other.magnitude()

    def __str__() -> str:
        return f"({x}, {y})"

    def __len__() -> int:
        return 2

    def __getitem__(index: int) -> float:
        match index:
            case 0: return x
            case 1: return y
            case _: raise IndexError(f"Index {index} out of range")

# Usage — operators dispatch to dunder methods
v1 = Vector(1.0, 2.0)
v2 = Vector(3.0, 4.0)
v3 = v1 + v2           # calls __add__
print(len(v1))          # calls __len__
print(v1[0])            # calls __getitem__
print(v1 == v2)         # calls __eq__
```

**Dunder method → .NET mapping:**

| Dunder Method | .NET Target | Operator |
|---|---|---|
| `__str__` | `ToString()` override | `str(x)`, f-strings |
| `__repr__` | `ToString()` override (with format flag) | `repr(x)` |
| `__eq__` | `Equals()` override + `op_Equality` | `==` |
| `__ne__` | `op_Inequality` | `!=` |
| `__lt__` | `IComparable<T>.CompareTo` + `op_LessThan` | `<` |
| `__le__` | `op_LessThanOrEqual` | `<=` |
| `__gt__` | `op_GreaterThan` | `>` |
| `__ge__` | `op_GreaterThanOrEqual` | `>=` |
| `__add__` | `op_Addition` | `+` |
| `__sub__` | `op_Subtraction` | `-` |
| `__mul__` | `op_Multiply` | `*` |
| `__truediv__` | `op_Division` | `/` |
| `__floordiv__` | Custom (no .NET equivalent) | `//` |
| `__mod__` | `op_Modulus` | `%` |
| `__pow__` | Custom (uses `Math.Pow`) | `**` |
| `__neg__` | `op_UnaryNegation` | `-x` |
| `__pos__` | `op_UnaryPlus` | `+x` |
| `__invert__` | `op_OnesComplement` | `~x` |
| `__and__` | `op_BitwiseAnd` | `&` |
| `__or__` | `op_BitwiseOr` | `\|` |
| `__xor__` | `op_ExclusiveOr` | `^` |
| `__lshift__` | `op_LeftShift` | `<<` |
| `__rshift__` | `op_RightShift` | `>>` |
| `__len__` | `Count` property or custom | `len(x)` |
| `__getitem__` | `this[index]` indexer | `x[i]` |
| `__setitem__` | `this[index]` indexer setter | `x[i] = v` |
| `__contains__` | Custom | `x in collection` |
| `__iter__` | `IEnumerable<T>.GetEnumerator()` | `for x in obj` |
| `__next__` | `IEnumerator<T>.MoveNext()` + `Current` | iterator protocol |
| `__enter__` | `IDisposable` (adapted) | `with obj:` |
| `__exit__` | `Dispose()` | `with obj:` exit |
| `__hash__` | `GetHashCode()` override | dict/set key |
| `__bool__` | Custom → `op_True` / `op_False` | `if obj:` |
| `__call__` | `Invoke()` method | `obj()` |
| `__init__` | `.ctor` | Construction |
| `__del__` | `Finalize()` override | GC cleanup |

**Performance considerations:**
- .NET operator overloads are static methods. The JIT inlines them aggressively when the concrete type is known. A `Vector.__add__` compiled as `op_Addition` will be as fast as a manual function call.
- `__eq__` must also generate `GetHashCode` if the type is used in dictionaries or sets. The compiler should warn if `__eq__` is defined without `__hash__`.
- For value types (structs), operator overloads avoid boxing entirely — they operate on the raw value.

**Security considerations:**
- `__del__` (finalizer) runs on the GC thread at an unpredictable time. It should never access managed resources that might have been collected. The compiler should emit a warning when `__del__` is defined, recommending `IDisposable` instead.
- `__eq__` and `__hash__` must be consistent: `a == b` implies `hash(a) == hash(b)`. The compiler should enforce this — if you define one, you must define both.

---

### 4.20 Async/Await Codegen

**Status:** `async def` and `await` expressions are parsed. `AwaitExpr` lowering evaluates the operand but does not emit the await pattern. `IsAsync` flag exists on IR functions. Not functional.

**What it does:** Async functions return `Task<T>` and can `await` other async operations, yielding control to the caller until the awaited operation completes.

```python
async def fetch_data(url: str) -> str:
    client = HttpClient()
    response = await client.get_async(url)
    return await response.content.read_as_string_async()

async def fetch_all(urls: list[str]) -> list[str]:
    tasks = [fetch_data(url) for url in urls]
    return await Task.when_all(tasks)

async def main():
    data = await fetch_data("https://api.example.com/data")
    print(data)
```

**Compilation target:** Async functions compile to `IAsyncStateMachine` implementations. This is the same transformation C# performs.

The compiler must generate:

1. **A state machine struct** implementing `IAsyncStateMachine`:
   - `int state` — current suspension point
   - `AsyncTaskMethodBuilder<T> builder` — manages the Task lifecycle
   - Fields for each local variable that lives across an `await` point
   - Fields for each `TaskAwaiter<T>` at each await point

2. **`MoveNext()` method** containing a state dispatch switch:
   - State 0: execute code up to first `await`, get awaiter, check `IsCompleted`
   - If not completed: store state, call `builder.AwaitUnsafeOnCompleted(ref awaiter, ref this)`, return
   - If completed: call `awaiter.GetResult()`, continue to next code/await
   - Final state: call `builder.SetResult(result)`
   - Exception: wrap in `try-catch`, call `builder.SetException(ex)`

3. **Stub method** (the original function signature):
   - Creates the state machine
   - Calls `builder.Start(ref stateMachine)`
   - Returns `builder.Task`

**Performance considerations:**
- The state machine should be a `struct` (value type) to avoid heap allocation for synchronous completions. When the first `await` completes synchronously (hot path cache hit, already-completed task), the entire async method runs without any heap allocation.
- `AsyncTaskMethodBuilder<T>` pools internal state on `ThreadPool` threads. The compiler doesn't need to manage this — just use the builder correctly.
- Each `await` point that crosses a suspension boundary requires saving all live locals to fields. Minimize live locals across await points — hoist expressions that don't depend on awaited values above the await.
- `ValueTask<T>` should be used instead of `Task<T>` when the method frequently completes synchronously. The compiler could detect this pattern (async method that only awaits already-completed tasks) and switch to `ValueTask` automatically.

**Security considerations:**
- Async methods that handle sensitive data (encryption keys, tokens) must ensure that the state machine struct is cleared after use. The `finally` block in `MoveNext` should zero sensitive fields. In practice, the GC will handle this, but for defense-in-depth, the compiler should support a `[SensitiveData]` annotation on async functions that generates explicit field clearing.
- Exception handling in async methods must not swallow exceptions silently. The `builder.SetException` path must always be reachable, and unobserved task exceptions should propagate to the `TaskScheduler.UnobservedTaskException` handler.

**Implementation notes:**
- This is the single most complex feature to implement. Each await point is a suspension/resumption point, and the compiler must correctly save and restore all local state.
- The IR needs `IrAwait(awaiterType)` and `IrAsyncReturn` instructions. The emitter transforms any function containing `IrAwait` into the state machine pattern.
- Start with single-await functions, then multi-await, then await-in-loops (each loop iteration is a state), then await-in-try (requires additional state machine complexity for exception propagation).
- `async for` and `async with` are extensions of this — they use `IAsyncEnumerable<T>` and `IAsyncDisposable` respectively, with the same state machine infrastructure.

---

### 4.21 `async for` and `async with`

**Status:** Not parsed. Not implemented.

**What it does:** Asynchronous iteration and asynchronous resource management.

```python
# Async iteration — processes items as they arrive
async def process_stream(stream: IAsyncEnumerable[Event]):
    async for event in stream:
        await handle(event)

# Async resource management
async def query_database(sql: str) -> list[Row]:
    async with AsyncSqlConnection(conn_str) as conn:
        return await conn.query(sql)
```

**Compilation target:**
- `async for x in iterable:` → `await iterable.GetAsyncEnumerator()` + loop with `await enumerator.MoveNextAsync()` + `enumerator.Current`
- `async with expr as name:` → `await expr.DisposeAsync()` in finally block (uses `IAsyncDisposable`)

**Depends on:** §4.20 (Async/Await Codegen) must be implemented first.

---

### 4.22 Struct Value-Type Semantics

**Status:** `struct` keyword parsed and types created. Currently emitted as sealed classes (reference types). Not functional as true value types.

**What it does:** Structs should be actual .NET value types — stack-allocated, copied on assignment, no GC pressure.

```python
struct Point:
    x: float
    y: float

p1 = Point(1.0, 2.0)
p2 = p1           # Copy — p2 is independent of p1
p2.x = 99.0       # p1.x is still 1.0

# Value types are stack-allocated when used locally
# No heap allocation, no GC pressure
def compute() -> float:
    origin = Point(0.0, 0.0)
    target = Point(3.0, 4.0)
    return distance(origin, target)
```

**Compilation target:** Emit `TypeAttributes.SequentialLayout` on the `TypeBuilder` and inherit from `System.ValueType`. Fields must use `StructLayoutAttribute` with explicit or sequential layout.

**Performance considerations:**
- Value types avoid heap allocation and GC pressure. For small types (≤ 16 bytes), they're significantly faster to create and destroy than reference types.
- Passing large structs by value causes copying. The compiler should pass structs > 16 bytes by `ref` (using `in` parameter modifier) when the callee doesn't mutate them.
- Value types should not implement finalizers (`__del__`). The compiler should reject this.
- Structs in generic contexts may cause boxing. The compiler should warn when a struct value is stored in a generic container typed as `object`.

---

### 4.23 Default Interface Implementations

**Status:** Interfaces parse abstract methods. Default method bodies in interfaces are not lowered. Not functional.

**What it does:** Interfaces can provide default implementations for methods, which implementing classes inherit without explicit override.

```python
interface Printable:
    def to_string() -> str

    # Default implementation — classes get this for free
    def print():
        print(to_string())

class User(Printable):
    name: str

    def to_string() -> str:
        return f"User({name})"
    # print() is inherited from Printable with default implementation
```

**Compilation target:** .NET supports default interface methods (DIM) since .NET Core 3.0 / C# 8.0. The compiler emits the default method body directly on the interface type.

**Performance considerations:**
- Default interface methods are dispatched via virtual call, same as regular interface methods. No additional overhead vs. abstract methods.
- If a class overrides the default, the override is used (standard virtual dispatch).

---

### 4.24 `del` Statement

**Status:** Not parsed. Not implemented. **Decision needed: include or exclude.**

**What it does in Python:** Unbinds a name from the current scope.

```python
x = 42
del x       # x is no longer defined
print(x)    # NameError
```

**Recommendation:** **Exclude.** Culebral is statically typed. Unbinding a variable at runtime creates a hole in the type system — the compiler cannot reason about whether a variable is defined after a `del` on a conditional path. This conflicts with design principle #3 ("If you write a type, you mean it"). If a variable should be nullable, declare it as `T?` and set it to `None`. This communicates the same intent with full type safety.

---

### 4.25 `assert` Statement

**Status:** Not parsed. Not implemented.

**What it does:** Runtime assertion for invariant checking during development.

```python
def binary_search(items: list[int], target: int) -> int:
    assert len(items) > 0, "Cannot search empty list"
    assert is_sorted(items), "Items must be sorted"
    # ... search logic
```

**Compilation target:** Compiles to a conditional throw:

```
// assert condition, message
evaluate condition
brtrue skip
ldstr message  // or evaluate message expression
newobj AssertionError::.ctor(string)
throw
skip:
```

In release builds (`culebral build --release`), assert statements should be stripped entirely (zero runtime cost). This is controlled by a compiler flag, not by removing the statements from source.

**Performance considerations:**
- Assert in debug builds: minimal overhead (one branch per assertion, predicted taken).
- Assert in release builds: zero overhead (compiled out entirely).
- The message expression should only be evaluated if the assertion fails (short-circuit). Do not evaluate the message when the condition is true.

**Security considerations:**
- Assert messages may contain sensitive state information (variable values, internal identifiers). In release builds, assertions are stripped entirely, so this is not a concern. In debug builds, ensure assert messages don't leak to end users.
- Never use `assert` for security checks (authentication, authorization, input validation). Assertions can be stripped. Security checks must use explicit `if` + `raise`.

**Implementation notes:**
- Add `AssertStatement` to the AST with `condition: Expression` and optional `message: Expression`.
- The lexer needs `assert` as a keyword.
- In the emitter, check a "debug mode" flag to decide whether to emit the assertion or skip it.

---

### 4.26 `global` and `nonlocal` Equivalents

**Status:** Not parsed. Not implemented. **Decision needed: include or exclude.**

**Recommendation:** **Exclude both.** Culebral does not have Python's scoping model. There is no module-level mutable state (design principle: "no monkey patching"). All mutable state lives in class fields or local variables. Closures capture by reference (like C#), which covers the `nonlocal` use case. `global` has no equivalent because there are no module-level mutable globals — use class-level static fields if needed.

---

### 4.27 Walrus Operator (`:=`)

**Status:** Not parsed. Not implemented. **Decision needed: include or exclude.**

**What it does in Python:** Assignment expression — assigns a value and returns it in a single expression.

```python
# Python
if (n := len(items)) > 10:
    print(f"List is too long ({n} items)")
```

**Recommendation:** **Exclude for now.** The walrus operator solves a real problem (avoiding double evaluation), but it's controversial even in Python. Culebral can revisit this if users frequently write patterns that would benefit from it. For now, use a separate assignment:

```python
# Culebral idiom
n = len(items)
if n > 10:
    print(f"List is too long ({n} items)")
```

---

### 4.28 String Methods (Python-style)

**Status:** .NET string methods work via case bridging (`s.to_upper()` → `s.ToUpper()`). Python-style method names (`s.upper()`, `s.strip()`) are NOT bridged. Partially functional.

**What it does:** Strings should support Python-style method names in addition to .NET-style names.

```python
name = "  Hello, World!  "

# These should all work:
name.upper()        # "  HELLO, WORLD!  " (Python-style)
name.to_upper()     # Same (snake_case .NET bridge)
name.strip()        # "Hello, World!" (Python-style)
name.trim()         # Same (.NET name)
name.split(",")     # ["  Hello", " World!  "]
name.startswith("  H")  # True (Python-style)
name.starts_with("  H") # Same (snake_case .NET bridge)
```

**Implementation approach:** The `DotNetTypeResolver` needs a string-specific method alias table:

| Python name | .NET name |
|---|---|
| `upper()` | `ToUpper()` |
| `lower()` | `ToLower()` |
| `strip()` | `Trim()` |
| `lstrip()` | `TrimStart()` |
| `rstrip()` | `TrimEnd()` |
| `startswith(s)` | `StartsWith(s)` |
| `endswith(s)` | `EndsWith(s)` |
| `find(s)` | `IndexOf(s)` |
| `rfind(s)` | `LastIndexOf(s)` |
| `replace(old, new)` | `Replace(old, new)` |
| `split(sep)` | `Split(sep)` |
| `join(items)` | `string.Join(sep, items)` |
| `count(sub)` | Custom (no direct .NET equivalent) |
| `isdigit()` | Custom (`char.IsDigit` loop) |
| `isalpha()` | Custom (`char.IsLetter` loop) |
| `zfill(width)` | `PadLeft(width, '0')` |
| `center(width)` | Custom |
| `encode(encoding)` | `Encoding.GetBytes(s)` |

**Performance considerations:**
- This is a compile-time name resolution, not a runtime layer. Zero overhead — `s.upper()` compiles to the exact same CIL as `s.ToUpper()`.

---

### 4.29 Built-in Functions — Complete Audit

Python has 71 built-in functions. Culebral implements a subset. This section is the authoritative reference for what exists, what's broken, what's missing, and what's deliberately excluded.

#### Currently implemented and working (14 functions)

| Function | .NET mapping | Overloads | Python parity | Notes |
|---|---|---|---|---|
| `print(...)` | `Console.WriteLine` / `IrPrint` | Multi-arg, sep, end, flush, file | **100%** | Full Python compat via IrPrint instruction |
| `len(x)` | `.Length` / `.Count` / `__len__` | 1 (polymorphic) | **95%** | Works on string, list, dict, set, arrays, user types with `__len__` |
| `str(x)` | `object.ToString()` | 1 | **100%** | |
| `int(x)` | `Convert.ToInt32(object)` | 1 | **70%** | Missing `base` param — `int("ff", 16)` fails |
| `float(x)` | `Convert.ToDouble(object)` | 1 | **100%** | |
| `abs(x)` | `Math.Abs(int/double)` | 1 (type-aware) | **95%** | No complex number support (excluded by design) |
| `min(a, b)` | `Math.Min(int/double)` | 1 (binary only) | **30%** | Missing: iterable form, variadic, `key=`, `default=` |
| `max(a, b)` | `Math.Max(int/double)` | 1 (binary only) | **30%** | Missing: iterable form, variadic, `key=`, `default=` |
| `range(...)` | `Enumerable.Range` | 3 (1/2/3 arg) | **70%** | Missing: negative step, step=0 validation |
| `round(x)` | `(int)Math.Round(double)` | 1 | **50%** | Missing `ndigits` param — `round(3.14, 2)` fails |
| `input(prompt)` | `Console.Write` + `Console.ReadLine` | 1 | **100%** | |
| `chr(n)` | `((char)n).ToString()` | 1 | **100%** | |
| `ord(c)` | `string[0]` cast to int | 1 | **100%** | |
| `type(x)` | `x.GetType().Name` | 1 | **70%** | Returns string, not type object |

#### Broken stubs — declared but crash at runtime (7 functions)

These are registered in the symbol table and pass type checking, but the emitter has no implementation. They silently emit a warning and push nothing useful onto the stack. **This is the worst kind of bug — code compiles but crashes.**

| Function | .NET mapping | Args | What it should do |
|---|---|---|---|
| `bool(x)` | Truthiness test | 1 | `x != 0`, `x != ""`, `x != None`, `len(x) != 0` — emit as conditional. For objects, check `__bool__` dunder, then `__len__` dunder, then default `true`. |
| `sorted(iterable)` | LINQ `.OrderBy(x => x).ToList()` | 1-3 | Sort iterable, return new list. Support `key=` function and `reverse=` bool. |
| `enumerate(iterable)` | LINQ `.Select((x, i) => (i, x))` | 1-2 | Yield `(index, value)` tuples. Support `start=` offset. |
| `zip(a, b)` | LINQ `.Zip(a, b)` | 2+ | Yield tuples from parallel iterables. Stop at shortest. |
| `map(fn, iterable)` | LINQ `.Select(fn)` | 2+ | Apply function to each element, yield results. |
| `filter(fn, iterable)` | LINQ `.Where(fn)` | 2 | Yield elements where `fn(element)` is truthy. `filter(None, iterable)` filters falsy values. |
| `isinstance(x, T)` | `x is T` | 2 | Runtime type check. Should support single type or tuple of types. |

**Fix priority: CRITICAL.** These must either be implemented or removed from the symbol table. Silently broken stubs are unacceptable.

#### Missing — should implement (16 functions)

These are common Python built-ins that don't exist in Culebral at all. Ordered by priority.

| Function | .NET mapping | Python signature | Priority | Notes |
|---|---|---|---|---|
| `all(iterable)` | `Enumerable.All(x => truthy(x))` | `all(iterable) → bool` | **Critical** | Returns True if all elements are truthy |
| `any(iterable)` | `Enumerable.Any(x => truthy(x))` | `any(iterable) → bool` | **Critical** | Returns True if any element is truthy |
| `sum(iterable)` | `Enumerable.Sum()` or loop | `sum(iterable, start=0) → number` | **Critical** | Sum with optional start value |
| `list(iterable)` | `new List<object>(iterable)` | `list(iterable?) → list` | **High** | Convert iterable to list. `list()` = empty list |
| `dict(...)` | `new Dictionary<object,object>()` | `dict(**kwargs)` / `dict(iterable)` | **High** | Convert to dict. `dict()` = empty dict |
| `set(iterable)` | `new HashSet<object>(iterable)` | `set(iterable?) → set` | **High** | Convert iterable to set. `set()` = empty set |
| `tuple(iterable)` | ValueTuple construction | `tuple(iterable?) → tuple` | **High** | Convert iterable to tuple |
| `reversed(seq)` | `Enumerable.Reverse()` | `reversed(sequence) → iterator` | **Medium** | Reverse iteration. Works on lists, strings, ranges |
| `hash(x)` | `x.GetHashCode()` | `hash(object) → int` | **Medium** | Returns hash value. Must be consistent with `__hash__` dunder |
| `repr(x)` | `x.ToString()` (with repr semantics) | `repr(object) → str` | **Medium** | Unambiguous string representation. Strings get quotes: `repr("hi")` → `"'hi'"` |
| `divmod(a, b)` | `(a / b, a % b)` as tuple | `divmod(a, b) → (quotient, remainder)` | **Medium** | Returns tuple of (quotient, remainder) |
| `pow(x, y, z)` | `Math.Pow` + modular | `pow(base, exp, mod=None) → number` | **Medium** | `**` operator exists, but 3-arg modular form `pow(base, exp, mod)` doesn't |
| `hex(n)` | `n.ToString("x")` | `hex(int) → str` | **Low** | Returns `"0xff"` format string |
| `bin(n)` | `Convert.ToString(n, 2)` | `bin(int) → str` | **Low** | Returns `"0b1010"` format string |
| `oct(n)` | `Convert.ToString(n, 8)` | `oct(int) → str` | **Low** | Returns `"0o17"` format string |
| `format(value, spec)` | `String.Format` / `IFormattable` | `format(value, format_spec='') → str` | **Low** | Custom string formatting |

#### Overload gaps on existing builtins

These builtins work but are missing Python-compatible overloads or parameters.

**`min` / `max` — currently binary only, Python supports 4 forms:**
```python
min(a, b)                    # ✅ Works
min(a, b, c, d)              # ❌ Variadic — not supported
min([1, 2, 3])               # ❌ Iterable — not supported
min([1, 2, 3], key=abs)      # ❌ Key function — not supported
min([], default=0)           # ❌ Default for empty — not supported
```
**Fix:** Detect arg count. 1 arg → iterable form (loop to find min). 2+ args → variadic (compare pairwise). Named `key=` and `default=` are lower priority.

**`range` — negative step broken:**
```python
range(5)                     # ✅ [0, 1, 2, 3, 4]
range(2, 8)                  # ✅ [2, 3, 4, 5, 6, 7]
range(0, 10, 2)              # ✅ [0, 2, 4, 6, 8]
range(5, 0, -1)              # ❌ Should be [5, 4, 3, 2, 1] — broken
range(10, 0, -2)             # ❌ Should be [10, 8, 6, 4, 2] — broken
range(0, 10, 0)              # ❌ Should raise ValueError — not validated
```
**Fix:** Replace `Enumerable.Range` with a custom range implementation that handles negative step via a descending loop. Validate step != 0 at compile time or runtime.

**`round` — missing ndigits:**
```python
round(3.14159)               # ✅ Returns 3
round(3.14159, 2)            # ❌ Should return 3.14 — not supported
round(3.14159, 0)            # ❌ Should return 3.0 (float!) — not supported
round(1234, -2)              # ❌ Should return 1200 — not supported
```
**Fix:** Check arg count. 1 arg → current behavior. 2 args → `Math.Round(x, ndigits)` and return float (not int) when ndigits is specified.

**`int` — missing base parameter:**
```python
int("42")                    # ✅ Returns 42
int("ff", 16)                # ❌ Should return 255 — not supported
int("0b1010", 2)             # ❌ Should return 10 — not supported
int("0o17", 8)               # ❌ Should return 15 — not supported
int("0xff", 16)              # ❌ Should return 255 — not supported
```
**Fix:** Check arg count. 1 arg → `Convert.ToInt32(x)`. 2 args → `Convert.ToInt32(string, base)`. Strip `0x`/`0b`/`0o` prefixes before conversion.

#### Not needed — use .NET interop directly (18 functions)

These Python built-ins have direct .NET equivalents accessible via `from System.X import Y`. Adding Culebral wrappers would be indirection for no benefit.

| Python | .NET equivalent | How to use in Culebral |
|---|---|---|
| `open(path)` | `System.IO.File` | `from System.IO import File; f = File.open_text(path)` |
| `bytes(...)` | `System.Text.Encoding` | `from System.Text import Encoding` |
| `bytearray(...)` | `byte[]` | Direct array type |
| `complex(r, i)` | `System.Numerics.Complex` | `from System.Numerics import Complex` |
| `frozenset(...)` | `ImmutableHashSet<T>` | `from System.Collections.Immutable import ImmutableHashSet` |
| `iter(x)` | `.GetEnumerator()` | Method call on any iterable |
| `next(it)` | `.MoveNext()` + `.Current` | Method calls on enumerator |
| `object()` | `object()` | Direct constructor |
| `super()` | Base class access | Handled by compiler |
| `classmethod` | Static methods | `def` in class body |
| `staticmethod` | Static methods | `def` in class body |
| `property(...)` | `prop` keyword | First-class in Culebral |
| `slice(...)` | Slice syntax | `items[1:3]` works directly |
| `ascii(x)` | `Encoding.ASCII` | .NET encoding |
| `memoryview(...)` | `Span<T>` / `Memory<T>` | .NET memory types |
| `callable(x)` | Type checking | Culebral is statically typed — callability is known at compile time |
| `vars(x)` / `dir(x)` | Reflection | `from System.Reflection import ...` |
| `getattr/setattr/delattr/hasattr` | Reflection | Dynamic attribute access not supported by design |

#### Deliberately excluded (20+ functions)

These Python built-ins conflict with Culebral's design principles or have no meaningful equivalent in a statically-typed compiled language.

| Function | Reason for exclusion |
|---|---|
| `eval(expr)` / `exec(code)` | Security hole. No runtime code evaluation. Design principle #5. |
| `compile(source)` | No runtime compilation. |
| `globals()` / `locals()` | No runtime namespace introspection. Variables are compiled away. |
| `breakpoint()` | Use IDE debugger + PDB files instead. |
| `__import__(name)` | Imports are resolved at compile time. |
| `id(x)` | Object identity is a CPython implementation detail. Use `is` for identity checks. |
| `issubclass(A, B)` | Use `is` operator or interfaces. Runtime type hierarchy checks encourage fragile code. |
| `help(x)` | Interactive REPL feature, not a language built-in. |
| `aiter(x)` / `anext(x)` | Async iteration handled by `async for` syntax. |
| All 30+ exception types | Exceptions come from .NET (`System.Exception`, `System.ArgumentException`, etc.). No need to re-declare them. |

#### Implementation notes

- Each built-in maps to a static .NET method, a LINQ extension, or inline CIL. The type checker recognizes the name and infers the return type. The emitter produces the corresponding CIL.
- Builtins with multiple overloads (min, max, range, round, int) must check arg count in the emitter and dispatch to the correct .NET method.
- Iterable builtins (sorted, reversed, enumerate, zip, map, filter, all, any, sum) should emit LINQ calls when possible for zero-allocation lazy evaluation. Fall back to list materialization when the result must be a concrete collection.
- `bool(x)` truthiness rules: `False`, `0`, `0.0`, `""`, `None`, empty collections → `False`. Everything else → `True`. Check `__bool__` dunder first, then `__len__`, then default `True`.

---

### 4.30 Source Maps and Debug Information (PDB)

**Status:** `SourceSpan` is tracked on AST nodes and IR instructions. No PDB emission exists. Not functional.

**What it does:** Generates debug symbols (PDB files) that map CIL instructions back to Culebral source lines, enabling step-through debugging in any .NET debugger (Visual Studio, Rider, VS Code).

**Implementation target:**
- Use `DebugDirectoryBuilder` with `PersistedAssemblyBuilder` to embed sequence points
- Mark each CIL instruction with the source file, line, and column from the `SourceSpan`
- Generate a `.pdb` file alongside the `.dll`

**Why this matters:**
- Without PDB files, debugging a Culebral program means reading CIL disassembly. With PDB files, you set breakpoints on `.cbl` source lines and step through in your IDE.
- This is a developer experience force multiplier. It makes Culebral programs first-class citizens in the .NET debugging ecosystem.

---

### 4.31 For-Else and While-Else

**Status:** Not parsed. Not implemented. **Decision needed: include or exclude.**

**What it does in Python:** The `else` clause on a loop runs if the loop completes without hitting `break`.

```python
for item in items:
    if item.matches(target):
        break
else:
    print("Not found")  # Only runs if loop completed without break
```

**Recommendation:** **Include.** This is a useful pattern for search loops that's surprisingly hard to express without it (requires a flag variable). It compiles to a simple boolean flag that is set by `break` and checked after the loop. Minimal implementation cost, genuine utility.

**Compilation target:**
```
found = false
for item in items:
    if condition:
        found = true
        break
if not found:
    <else block>
```

---

### 4.32 Multi-Type Generic Arguments

**Status:** Parser's `ParseIndexOrSlice` only handles single expressions in brackets. `method[T1, T2](args)` does not parse. Not functional.

**What it does:** Allows generic method calls and type instantiation with multiple type arguments.

```python
# Single type arg works today
arr = Array.empty[int]()

# Multiple type args — not yet supported
dict = Dictionary[str, int]()
result = converter.convert[str, int](value)
```

**Implementation notes:**
- The parser's `ParseIndexOrSlice` needs to handle comma-separated type expressions inside brackets when the context is a generic type or method call.
- This is a parser-level change. The IR and emitter already support `Type[]` arrays for type arguments.

---

## Deliberately Excluded Features

These Python features are **intentionally excluded** from Culebral. This is not a gap — it's a design decision.

| Feature | Reason for exclusion |
|---|---|
| `self` parameter | Implicit `this` with `@field` disambiguation — cleaner, less boilerplate |
| Dynamic typing | Types at boundaries, inference inside — catches bugs at compile time |
| `**kwargs` | Use typed optional parameters or config objects — kwargs hide API shape |
| Metaclasses | Earn your complexity — decorators + interfaces cover the use cases |
| Multiple inheritance | Single inheritance + interfaces (like C#/Rust) — no diamond problem |
| Duck typing | Interfaces provide the same flexibility with compile-time verification |
| `__slots__` | All classes have defined layouts — it's a compiled language |
| Monkey patching | No runtime type mutation — enables aggressive optimization and prevents entire categories of bugs |
| `eval()` / `exec()` | Security hole, performance killer, makes static analysis impossible |
| `**kwargs` | Typed config objects are safer and self-documenting |
| `global` / `nonlocal` | No module-level mutable state; closures capture by reference |
| `del` statement | Use nullable types (`T?`) for explicit absence |
| Walrus operator (`:=`) | Use separate assignment — clearer, no ambiguity |
| `complex` type | Niche — use `System.Numerics.Complex` via .NET interop if needed |
| `@` matrix multiply | Niche — use a numeric library via .NET interop |
| Starred subscripts (`obj[*iterable]`) | Niche Python 3.11+ feature, unclear .NET mapping |
| `ParamSpec` / `TypeVarTuple` | Advanced typing features — revisit if demand materializes |
| `match` OR patterns (`p1 \| p2`) | Adds parser complexity — may revisit later |
| `match` mapping patterns (`{k: v}`) | Adds complexity — use guard clauses instead |

---

## Open Questions

1. **File extension:** `.cbl` (short, clean) or `.skel`?
2. **Package manager:** Wrap `dotnet` CLI, or custom tooling?
3. **REPL:** Worth building early? Good for adoption, but tricky with static types.
4. **String type:** Use `System.String` directly, or wrap it for Python-like methods (`s.upper()` vs `s.ToUpper()`)?
5. **Native GC strategy:** Boehm (easy, conservative), LLVM statepoints (correct, hard), or ref counting (predictable, needs cycle detection)?
6. **Native module granularity:** Per-file (`@native` on a module) or per-project (separate `culebral.toml` target)?
7. **For-else / while-else:** Include loop-else syntax? Low implementation cost, genuine utility, but non-obvious semantics for newcomers.
8. **`assert` behavior in release builds:** Strip entirely (like C), or compile to no-op (preserves side effects in condition)? Recommendation: strip entirely — assertions should never have side effects.
