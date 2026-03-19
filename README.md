<p align="center">
  <br>
  <code>C U L E B R A L</code>
  <br><br>
  <em>The skeleton of Python, reforged for .NET.</em>
  <br><br>
  <a href="#quickstart">Quickstart</a> · <a href="#the-language">Language</a> · <a href="#architecture">Architecture</a> · <a href="#building">Building</a>
  <br><br>
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat" alt=".NET 10">
  <img src="https://img.shields.io/badge/C%23-14-239120?style=flat" alt="C# 14">
  <img src="https://img.shields.io/badge/tests-215%20passing-22863a?style=flat" alt="Tests">
  <img src="https://img.shields.io/badge/phase-4%20complete-22863a?style=flat" alt="Phase 4">
</p>

---

Culebral is a statically typed, compiled programming language that reads like Python and runs on .NET. Function signatures are contracts. Bodies are inferred. The entire BCL and NuGet ecosystem is one import away.

```python
def fibonacci(n: int) -> int:
    if n <= 1:
        return n
    return fibonacci(n - 1) + fibonacci(n - 2)

def main():
    for i in range(10):
        print(f"fib({i}) = {fibonacci(i)}")
```

```
$ culebral run fibonacci.leb
fib(0) = 0
fib(1) = 1
fib(2) = 1
fib(3) = 2
fib(4) = 3
fib(5) = 5
fib(6) = 8
fib(7) = 13
fib(8) = 21
fib(9) = 34
```

## Design Philosophy

| # | Principle |
|---|-----------|
| 1 | **Python's readability, Rust's discipline, .NET's power.** |
| 2 | **Types at boundaries, inference inside.** Signatures are the API. The compiler handles the rest. |
| 3 | **If you write a type, you mean it.** No optional hints — every annotation is enforced. |
| 4 | **Interop is not an afterthought.** NuGet packages should feel native. |
| 5 | **Earn your complexity.** No metaclasses, no MRO diamonds. If it doesn't pull its weight, it's out. |

## Quickstart

**Requirements:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```bash
# Clone and build the compiler
git clone git@github.com:Giancarlos/Culebral.git
cd Culebral
dotnet build

# Write your first program
cat > hello.leb << 'EOF'
def main():
    print("Hello from Culebral!")
EOF

# Compile and run
dotnet run --project src/Culebral.Compiler -- run hello.leb
```

## The Language

### Functions — The Core Rule

Parameters and return types are always explicit. Local variables are inferred.

```python
def greet(name: str, times: int) -> str:
    parts = [f"Hello, {name}!" for _ in range(times)]  # inferred: list[str]
    result = "\n".join(parts)                           # inferred: str
    return result
```

### No `self` — Ever

Instance fields are bare names. Use `@` when shadowed. That's it.

```python
class Counter:
    count: int = 0

    def __init__(initial: int = 0):
        count = initial

    def increment() -> int:
        count += 1
        return count

    def display(count: int):
        print(f"Parameter: {count}, Field: {@count}")
```

### Null Safety

No implicit nulls. Declare intent with `?`.

```python
def find_user(id: int) -> User?:
    if id in database:
        return database[id]
    return None

def greet_user(id: int) -> str:
    user = find_user(id)
    if user is None:
        return "Unknown"
    return f"Hello, {user.name}"  # narrowed to User here
```

### Algebraic Enums

```python
enum Shape:
    Circle(radius: float)
    Rectangle(width: float, height: float)
    Point

match shape:
    case Shape.Circle(r):
        print(f"Circle with radius {r}")
    case Shape.Rectangle(w, h):
        print(f"Rectangle {w}x{h}")
    case Shape.Point:
        print("Just a point")
```

### Structs, Records, Interfaces

```python
struct Point:
    x: float
    y: float

    def distance_to(other: Point) -> float:
        return ((x - other.x) ** 2 + (y - other.y) ** 2) ** 0.5

record User:
    name: str
    email: str
    age: int

interface Drawable:
    def draw(canvas: Canvas) -> None
    def bounds() -> Rect
```

### .NET Interop

Any .NET type. Any NuGet package. Case convention bridging is automatic.

```python
from System.Net.Http import HttpClient

async def fetch(url: str) -> str:
    client = HttpClient()
    response = await client.get_async(url)    # maps to GetAsync
    return await response.content.read_as_string_async()
```

```toml
# culebral.toml
[project]
name = "my-app"
target = "net10.0"

[dependencies]
"Newtonsoft.Json" = "13.0.3"
"Dapper" = "2.1.*"
```

### What We Keep, Drop, and Change

<details>
<summary><b>Kept from Python</b></summary>

Indentation blocks · f-strings · comprehensions · slicing · `for x in collection` · `with` statement · multiple assignment · `*args` · decorators · `__init__` / `__str__` / `__repr__` · `range()`

</details>

<details>
<summary><b>Dropped from Python</b></summary>

`self` parameter · dynamic typing · `**kwargs` · metaclasses · multiple inheritance · GIL · duck typing · `__slots__` · monkey patching · `eval()` / `exec()`

</details>

<details>
<summary><b>Changed from Python</b></summary>

| Python | Culebral | Why |
|--------|---------|-----|
| `None` | `None` (but `T?` required) | Must opt in to nullability |
| `class` | `class` / `struct` / `enum` | Separate value and sum types |
| `typing.Protocol` | `interface` | First-class, not bolted on |
| `@property` | `prop` keyword | Cleaner syntax |
| `dataclasses` | `struct` or `record` | Built into the language |
| `match` | `match` (enhanced) | Exhaustiveness checking |
| `lambda` | `lambda` (multi-line) | Proper lambdas |

</details>

## Architecture

```
Source (.leb)
    │
    ▼
┌──────────┐
│  Lexer   │  Indentation → INDENT/DEDENT tokens
└────┬─────┘
     ▼
┌──────────┐
│  Parser  │  Recursive descent → AST (~70 node types)
└────┬─────┘
     ▼
┌──────────┐
│  Type    │  Two-pass: declarations then bodies
│  Checker │  Infer locals, check signatures, null safety
└────┬─────┘
     ▼
┌──────────┐
│  IR      │  Basic blocks, stack instructions, typed locals
│ Lowering │  Desugar comprehensions, for-loops, patterns
└────┬─────┘
     ▼
┌──────────┐
│  CIL     │  PersistedAssemblyBuilder → .NET assembly
│ Emitter  │  ManagedPEBuilder → PE with entry point
└────┬─────┘
     ▼
  .dll / .exe  →  dotnet run
```

### Project Structure

```
culebral/
├── src/Culebral.Compiler/
│   ├── Lexer/           # Indentation-aware tokenizer
│   ├── Parser/          # Recursive descent parser + AST
│   ├── Semantics/       # Type system, symbol table, checker
│   ├── IR/              # CulebralIR + AST→IR lowering
│   ├── Emit/            # CIL emitter via Reflection.Emit
│   ├── Diagnostics/     # Error/warning reporting
│   └── Program.cs       # CLI entry point
├── tests/Culebral.Compiler.Tests/
│   ├── LexerTests.cs    # 14 tests
│   ├── ParserTests.cs   # 30 tests
│   ├── TypeCheckerTests.cs  # 10 tests
│   └── EmitTests.cs     # 20 end-to-end tests
├── samples/             # Example .leb programs
└── spec.md              # Full language specification
```

Zero external dependencies for core compilation. The compiler is a single .NET 10 console application.

## Building

```bash
# Build the compiler
dotnet build

# Run tests
dotnet test

# Compile a Culebral program
dotnet run --project src/Culebral.Compiler -- build samples/hello.leb

# Compile and run in one step
dotnet run --project src/Culebral.Compiler -- run samples/hello.leb

# Type-check without compiling
dotnet run --project src/Culebral.Compiler -- check samples/fibonacci.leb
```

### Debug Commands

```bash
# Print lexer tokens
dotnet run --project src/Culebral.Compiler -- lex samples/hello.leb

# Print parse tree
dotnet run --project src/Culebral.Compiler -- parse samples/hello.leb

# Print CulebralIR
dotnet run --project src/Culebral.Compiler -- ir samples/fibonacci.leb
```

## Roadmap

| Phase | Focus | Status |
|-------|-------|--------|
| **1** | Minimum viable language — functions, control flow, basic types, CIL emission | **Core complete** |
| **2** | Type system + classes — generics, constructors, interfaces, `@field` | Next |
| **3** | .NET interop — BCL imports, case bridging, NuGet resolution | Planned |
| **4** | Language completeness — async/await, pattern matching, records, null safety | Planned |
| **5** | Standard library — `Result` type (built-in), `culebral.testing` | Minimal — .NET interop is the stdlib |
| **6** | Native modules — LLVM backend for `@native` hot paths | Future |

## License

[MIT](LICENSE)

---

<p align="center">
  <sub>Built with .NET 10 · Zero dependencies · Compiles to real CIL</sub>
</p>
