# Skelvon — A Python-Inspired Language for .NET

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
from skelvon.result import Result, Ok, Err

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

.NET uses PascalCase. Skelvon code uses snake_case. The compiler bridges both directions automatically.

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
# skelvon.toml (project file)
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

| Python | Skelvon | Rationale |
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
from skelvon.web import get, post, body, path

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
    from skelvon.ffi.libc import fopen, fread, fclose

    def read_text(path: str) -> str:
        # native implementation via C FFI
        ...
```

Valid targets: `"net"` (primary, default), `"native"` (future LLVM backend). User code can branch on target, but in practice this should live almost entirely in library code — application code should use `skelvon.*` abstractions.

---

## Standard Library (`skelvon.*`)

The `skelvon.*` namespace is reserved for the standard library. On .NET, these are thin wrappers over BCL types — typically one-liners. The standard library is written in Skelvon itself and ships with the compiler.

### Module Resolution

Import resolution follows a search path: project source → `lib/` directory → standard library path. `skelvon.*` imports resolve to `.skv` files on disk. .NET imports (anything else like `System.*`, `Microsoft.*`, NuGet packages) resolve against .NET assembly metadata.

### Philosophy

The standard library is **not** a priority. For the .NET target, users have direct access to the entire BCL and NuGet ecosystem — the standard library only exists where a Pythonic wrapper meaningfully improves ergonomics. It should emerge from real pain points, not be designed upfront.

### Example Modules (Illustrative, Not Implemented)

```python
# skelvon/collections/hashmap.skv
when target == "net":
    from System.Collections.Generic import Dictionary
    type HashMap[K, V] = Dictionary[K, V]
```

```python
# skelvon/io/file.skv
when target == "net":
    from System.IO import File as _File

    def read_text(path: str) -> str:
        return _File.read_all_text(path)

    def write_text(path: str, content: str):
        _File.write_all_text(path, content)
```

```python
# skelvon/http/client.skv
when target == "net":
    from System.Net.Http import HttpClient as _Client

    async def get(url: str) -> str:
        client = _Client()
        response = await client.get_async(url)
        return await response.content.read_as_string_async()
```

### Planned Modules

| Module | Wraps (.NET) | Priority |
|---|---|---|
| `skelvon.io` | `System.IO` | When needed |
| `skelvon.collections` | `System.Collections.Generic` | When needed |
| `skelvon.math` | `System.Math` | When needed |
| `skelvon.json` | `System.Text.Json` | When needed |
| `skelvon.http` | `System.Net.Http` | When needed |
| `skelvon.async` | `System.Threading.Tasks` | When needed |
| `skelvon.testing` | Custom test runner | When needed |
| `skelvon.result` | Custom `Result[T, E]` type | Early — used in examples |

---

## Compiler Architecture

The compiler is split at the **SkelvonIR** boundary. Everything above the IR is shared across all backends. Everything below is target-specific. This costs almost nothing to implement versus emitting CIL directly, but keeps the door open for future backends.

```
Source (.skv files)
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
│  SkelvonIR   │  Typed, lowered, target-agnostic representation
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

### SkelvonIR

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

Used to resolve well-known BCL types during compilation. When Skelvon's `str` maps to `System.String`, or `print()` maps to `Console.WriteLine()`, the compiler resolves these through `typeof(string)`, `typeof(Console).GetMethod(...)`, etc.

### Optional NuGet Dependencies

These are not required but may be added for convenience:

| Package | Purpose | When to Add |
|---|---|---|
| `System.CommandLine` | CLI argument parsing (`skelvon build`, `skelvon run`) | Phase 1 — nice to have |
| `xUnit` + `FluentAssertions` | Testing framework | Phase 1 — essential |
| `Mono.Cecil` | Richer API for reading assembly metadata | Phase 3 — only if `System.Reflection.Metadata` proves too low-level |

### Project Structure

```
skelvon/
├── src/
│   └── Skelvon.Compiler/              # Main compiler — .NET 10 console app
│       ├── Skelvon.Compiler.csproj     # <TargetFramework>net10.0</TargetFramework>
│       ├── Program.cs                  # CLI entry: skelvon build / run / check
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
│       │   ├── SkelvonIR.cs            # IR node types
│       │   └── Lowering.cs             # AST → IR (desugar, flatten)
│       ├── Emit/
│       │   └── CilEmitter.cs           # IR → .NET assembly via PersistedAssemblyBuilder
│       └── Diagnostics/
│           └── DiagnosticBag.cs        # Error/warning collection + reporting
├── tests/
│   └── Skelvon.Compiler.Tests/         # xUnit test project
│       ├── Skelvon.Compiler.Tests.csproj
│       ├── LexerTests.cs
│       ├── ParserTests.cs
│       ├── TypeCheckerTests.cs
│       └── EmitTests.cs               # End-to-end: source → assembly → run → assert
├── samples/
│   ├── hello.skv                       # def main(): print("Hello from Skelvon!")
│   ├── fibonacci.skv
│   └── classes.skv
├── skelvon.sln
└── README.md
```

### First Compilation Target (Hello World)

The first program the compiler must handle:

```python
# hello.skv
def main():
    print("Hello from Skelvon!")
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
il.Emit(OpCodes.Ldstr, "Hello from Skelvon!");
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
dotnet new sln -n skelvon
dotnet new console -n Skelvon.Compiler -o src/Skelvon.Compiler
dotnet new xunit -n Skelvon.Compiler.Tests -o tests/Skelvon.Compiler.Tests
dotnet sln add src/Skelvon.Compiler tests/Skelvon.Compiler.Tests
dotnet add tests/Skelvon.Compiler.Tests reference src/Skelvon.Compiler

# Build the compiler
dotnet build

# Compile a Skelvon program
dotnet run --project src/Skelvon.Compiler -- build samples/hello.skv

# Run the output
dotnet samples/hello.dll

# Eventually, once installed as a tool:
skelvon build hello.skv
skelvon run hello.skv
```

### Phase Plan

**Phase 1 — Minimum Viable Language (8-12 weeks)**
- Lexer with INDENT/DEDENT
- Parser for: functions, variables, if/else, for/while, basic expressions
- Type checking for primitives and basic inference
- SkelvonIR generation
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
- `is` / `is not` operators (runtime type checks and null checks)
- `in` / `not in` operators (collection membership)
- `raise` statement (throw exceptions)
- `try`/`except`/`finally` (exception handling)
- `**` (power) and `//` (floor division) operator emission
- `range(start, stop)` and `range(start, stop, step)` overloads
- Multiple assignment / tuple unpacking (`a, b = b, a`)
- `*args` unpacking (maps to `params T[]`)
- `__str__` / `__repr__` → `ToString()` mapping
- Pattern matching with exhaustiveness
- Algebraic enum match codegen
- Async/await codegen
- Lambda expressions (single and multi-line) → delegates
- Comprehensions (list, dict, set, generator) codegen
- `with` statement → `IDisposable` / `using`
- Null safety with flow typing
- Slicing (`items[1:3]`, `items[::-1]`)
- Named tuple returns and destructuring
- Record `with` expressions (`alice with (name="Bob")`)
- Type alias (`type HashMap[K, V] = Dictionary[K, V]`)
- Decorator emission (attributes and wrappers)
- Generic constraints enforcement
- LSP server for editor support

**Phase 5 — Standard Library (when needed)**
- `skelvon.result` (Result type, likely needed early)
- `skelvon.io`, `skelvon.collections`, etc. as pain points emerge
- Written in Skelvon, thin wrappers over BCL
- Builds incrementally, no big upfront design

**Phase 6 — Native Modules (LOWEST PRIORITY)**

> ⚠ **This phase is the very last priority.** Everything above must be solid before any native work begins. The architectural decisions (SkelvonIR boundary, `when target` syntax, module resolution paths) are already in place to support this — no further work is needed until this phase is actively started.

- LLVM backend emitting shared libraries from `@native` modules
- Auto-generated P/Invoke bridge from .NET side to native modules
- Marshaling layer for types crossing the managed/native boundary
- GC integration (Boehm initially, precise GC later)
- C FFI support for native modules calling external C libraries
- `skelvon.std` native implementations behind `when target == "native"` branches
- **Goal:** `@native` module compiles to `.so`/`.dll`, callable from .NET Skelvon code seamlessly

---

## Native Modules (Future — Phase 6)

> ⚠ **This entire section describes future work that is the lowest priority item in the project.** It is included here for architectural planning only. Do not implement any of this until Phases 1-5 are complete and stable.

Native modules allow performance-critical code to compile to native binaries via LLVM while the main application remains a .NET project. The managed runtime is the host; native code is called for hot paths.

### Syntax

```python
# app.skv — compiles to .NET as usual
from engine import process_frame

async def main():
    data = await get("https://api.example.com/frames")
    result = process_frame(data)
    print(result)
```

```python
# engine.skv — native module
@native
module engine:

    def process_frame(data: bytes) -> bytes:
        # compiles to native .so/.dll via LLVM
        # no GC pressure, SIMD available
        ...
```

### How It Works

1. Compiler sees `@native` annotation on a module
2. `engine.skv` is compiled through the LLVM backend → shared library (`engine.so` / `engine.dll` / `engine.dylib`)
3. Compiler auto-generates P/Invoke bridge on the .NET side (user never writes `DllImport`)
4. `skelvon build` runs both backends and links outputs together

### Boundary Constraints

Native modules enforce a strict boundary for types crossing managed/native:

| Allowed | Not Allowed |
|---|---|
| Primitives (int, float, bool) | Classes |
| `bytes` (byte arrays) | Interfaces |
| Fixed-layout `struct` types | Generics crossing the boundary |
| `str` (marshaled as UTF-8) | Closures / lambdas |

Native modules cannot import .NET types. They can use `skelvon.std` native implementations and C FFI bindings only.

### Build System

```toml
# skelvon.toml
[project]
name = "my-app"
version = "0.1.0"
target = "net10.0"

[dependencies.net]
"Newtonsoft.Json" = "13.0.3"

[modules.native]
engine = { path = "engine.skv" }

[dependencies.native]
libcurl = { ffi = true }
```

One `skelvon build` compiles everything — .NET modules through CIL, native modules through LLVM, bridge code auto-generated.

---

## Open Questions

1. **File extension:** `.skv` (short, clean) or `.skel`?
2. **Package manager:** Wrap `dotnet` CLI, or custom tooling?
3. **REPL:** Worth building early? Good for adoption, but tricky with static types.
4. **String type:** Use `System.String` directly, or wrap it for Python-like methods (`s.upper()` vs `s.ToUpper()`)?
5. **Native GC strategy:** Boehm (easy, conservative), LLVM statepoints (correct, hard), or ref counting (predictable, needs cycle detection)?
6. **Native module granularity:** Per-file (`@native` on a module) or per-project (separate `skelvon.toml` target)?
