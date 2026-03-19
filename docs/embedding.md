# Embedding Culebral in .NET Applications

> Use Culebral as a scripting language inside any .NET application — games, tools, servers, automation pipelines.

## The Vision

Any .NET application should be able to do this:

```csharp
using Culebral.Scripting;

var engine = new CulebralEngine();

// Expose C# objects to scripts
engine.SetGlobal("player", myPlayerObject);
engine.SetGlobal("world", myWorldObject);

// Execute a script
engine.Execute("""
    player.health += 10
    if player.health > 100:
        player.health = 100
    print(f"Player healed to {player.health}")
""");

// Evaluate an expression and get a result
int damage = engine.Eval<int>("player.attack * 2 + weapon.bonus");

// Load and run a script file
engine.ExecuteFile("scripts/on_player_death.cbl");

// Register a C# function callable from scripts
engine.SetFunction("spawn_enemy", (string type, int level) => {
    return EnemyFactory.Create(type, level);
});
```

This is how Lua works in games, how Python works in Blender, and how JavaScript works in browsers. Culebral's advantage: it's already .NET native. No marshaling, no FFI boundary, no serialization. Host objects are script objects — same runtime, same GC, same type system.

---

## What Needs to Change

The current compiler is a **CLI tool** that reads `.cbl` files, produces `.dll` assemblies, and writes them to disk. Embedding requires an **in-process API** that compiles and executes code without touching the filesystem.

### Current Architecture

```
Source (.cbl file on disk)
    → Lexer → Parser → TypeChecker → IR Lowering → CIL Emitter
        → .dll file on disk
            → dotnet <file>.dll (separate process)
```

### Embedded Architecture

```
Source (string from host application)
    → Lexer → Parser → TypeChecker → IR Lowering → CIL Emitter
        → In-memory assembly (AssemblyBuilder)
            → Direct invocation via reflection (same process)
```

The compiler pipeline is identical. The only changes are at the **entry point** (string input instead of file) and the **exit point** (in-memory execution instead of disk output).

---

## Components to Build

### 1. `CulebralEngine` — The Embedding API

The central class that host applications interact with. Manages compilation, execution, and the shared state between host and scripts.

```csharp
namespace Culebral.Scripting;

public sealed class CulebralEngine : IDisposable
{
    // ─── Configuration ───

    /// <summary>Create a new engine with default settings.</summary>
    public CulebralEngine();

    /// <summary>Create a new engine with custom options.</summary>
    public CulebralEngine(CulebralEngineOptions options);

    // ─── Global Variables (Host ↔ Script) ───

    /// <summary>Expose a host object to scripts as a global variable.</summary>
    public void SetGlobal(string name, object value);

    /// <summary>Read a global variable set by a script.</summary>
    public object? GetGlobal(string name);

    /// <summary>Remove a global variable.</summary>
    public void RemoveGlobal(string name);

    // ─── Host Functions ───

    /// <summary>Register a C# function callable from Culebral scripts.</summary>
    public void SetFunction(string name, Delegate function);

    /// <summary>Register a C# function with explicit parameter types.</summary>
    public void SetFunction<TResult>(string name, Func<TResult> function);
    public void SetFunction<T, TResult>(string name, Func<T, TResult> function);
    public void SetFunction<T1, T2, TResult>(string name, Func<T1, T2, TResult> function);
    // ... overloads up to 8 parameters

    // ─── Execution ───

    /// <summary>Execute a Culebral script. Returns when the script finishes.</summary>
    public void Execute(string source);

    /// <summary>Execute a script file from disk.</summary>
    public void ExecuteFile(string path);

    /// <summary>Evaluate an expression and return the result.</summary>
    public T Eval<T>(string expression);

    /// <summary>Evaluate an expression and return the result as object.</summary>
    public object? Eval(string expression);

    // ─── Compilation (Advanced) ───

    /// <summary>Pre-compile a script for repeated execution.</summary>
    public CompiledScript Compile(string source);

    /// <summary>Execute a pre-compiled script.</summary>
    public void Execute(CompiledScript script);

    // ─── Diagnostics ───

    /// <summary>Fires when the script calls print().</summary>
    public event Action<string>? OnOutput;

    /// <summary>Fires when a compilation warning is emitted.</summary>
    public event Action<Diagnostic>? OnWarning;

    // ─── Lifecycle ───

    public void Dispose();
}
```

### 2. `CulebralEngineOptions` — Configuration

```csharp
public sealed class CulebralEngineOptions
{
    /// <summary>Maximum script execution time before cancellation.</summary>
    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum memory a script can allocate (approximate).</summary>
    public long MaxMemoryBytes { get; set; } = 256 * 1024 * 1024; // 256 MB

    /// <summary>Allow scripts to import .NET namespaces.</summary>
    public bool AllowDotNetImports { get; set; } = false;

    /// <summary>Allow scripts to access the filesystem.</summary>
    public bool AllowFileSystem { get; set; } = false;

    /// <summary>Allow scripts to make network requests.</summary>
    public bool AllowNetwork { get; set; } = false;

    /// <summary>Namespaces that scripts are allowed to import (when AllowDotNetImports is true).</summary>
    public HashSet<string> AllowedNamespaces { get; set; } = new();

    /// <summary>Types that are always available to scripts without import.</summary>
    public HashSet<Type> ExposedTypes { get; set; } = new();

    /// <summary>Enable script compilation caching for repeated execution.</summary>
    public bool EnableCompilationCache { get; set; } = true;

    /// <summary>Maximum number of cached compiled scripts.</summary>
    public int CompilationCacheSize { get; set; } = 100;

    /// <summary>Redirect print() output instead of writing to Console.</summary>
    public TextWriter? OutputWriter { get; set; }
}
```

### 3. `CompiledScript` — Pre-compiled Scripts

For scripts that run repeatedly (game update loops, rule evaluation, templating), compiling once and executing many times avoids redundant work.

```csharp
public sealed class CompiledScript
{
    /// <summary>The original source code.</summary>
    public string Source { get; }

    /// <summary>Compilation diagnostics (warnings, if any).</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Time taken to compile.</summary>
    public TimeSpan CompilationTime { get; }

    // Internal: the in-memory assembly and entry point method
    internal Assembly Assembly { get; }
    internal MethodInfo EntryPoint { get; }
}
```

### 4. `ScriptContext` — Shared State

The bridge between the host application and the executing script. Holds global variables, registered functions, and output capture.

```csharp
internal sealed class ScriptContext
{
    // Global variables visible to scripts
    internal Dictionary<string, object?> Globals { get; } = new();

    // Host functions callable from scripts
    internal Dictionary<string, Delegate> Functions { get; } = new();

    // Captured output from print()
    internal TextWriter Output { get; set; } = Console.Out;
}
```

---

## Compiler Changes Required

### Change 1: In-Memory Assembly Generation

**Current:** `CilEmitter` writes a `.dll` file to disk using `PersistedAssemblyBuilder.Save()`.

**Needed:** Add an alternative code path that keeps the assembly in memory using `AssemblyBuilder` (non-persisted) for direct invocation via reflection.

```csharp
// Current (disk output)
public bool Emit(IrModule module); // writes .dll

// New (in-memory output)
public Assembly EmitInMemory(IrModule module); // returns loaded assembly
```

The in-memory path uses `AssemblyBuilder.DefineDynamicAssembly()` instead of `new PersistedAssemblyBuilder()`. The `ILGenerator` code is identical — only the assembly creation and finalization differ.

**Effort: Small.** The IL emission logic doesn't change. Only the assembly builder setup and the "save" step change.

### Change 2: String Input Instead of File Input

**Current:** `Program.Compile(string inputPath, string outputPath)` reads from a file.

**Needed:** A new entry point that accepts source as a string.

```csharp
// New API
public static CompilationResult CompileFromSource(
    string source,
    string sourceName = "<script>",  // for error messages
    ScriptContext? context = null);   // for globals/functions
```

**Effort: Trivial.** The pipeline already receives a `string source` after the file read. Just skip the `File.ReadAllText()` step.

### Change 3: Global Variable Injection

Scripts need to access host-provided global variables. The type checker and lowering must recognize names from the `ScriptContext.Globals` dictionary.

**Approach:** Before type-checking, register each global in the symbol table with its runtime type. During lowering, global access emits a call to `ScriptContext.GetGlobal(name)` (resolved via a static field or passed as a parameter).

```python
# Script sees 'player' as a global — no import needed
player.health += 10
if player.position.x > 100:
    player.position.x = 100
```

The compiler resolves `player` as an `object` (or the specific .NET type if the host provides type info). Member access uses the existing .NET interop infrastructure — `player.health` resolves via reflection, same as any .NET type.

**Effort: Medium.** Requires a new "global scope" in the symbol table that precedes local scopes, and a mechanism to inject CLR types at compile time.

### Change 4: Host Function Registration

Host-registered functions must be callable from scripts without `import`. The type checker needs to recognize them, and the emitter needs to invoke them via delegate.

```csharp
// Host registers:
engine.SetFunction("spawn_enemy", (string type, int level) => {
    return EnemyFactory.Create(type, level);
});

// Script calls:
boss = spawn_enemy("dragon", 50)
```

**Approach:** Registered functions are injected into the symbol table with their parameter and return types (extracted from the delegate signature via reflection). At emission time, the call compiles to a delegate invocation on the `ScriptContext.Functions` dictionary entry.

**Effort: Medium.** Delegate invocation already works (`IrInvokeDelegate`). The main work is injecting the function signatures into the type checker.

### Change 5: `print()` Output Redirection

In embedded mode, `print()` should not write to `Console.Out` by default. Instead, it should write to a configurable `TextWriter` (which could be a string buffer, a log system, a UI console, etc.).

**Approach:** In embedded mode, `print()` emits a call to `ScriptContext.Output.Write/WriteLine()` instead of `Console.Write/WriteLine()`.

**Effort: Small.** Change the `IrPrint` emission to check if a context is available and redirect accordingly.

### Change 6: Sandboxing

Untrusted scripts must not be able to:
- Access the filesystem
- Make network requests
- Call `System.Diagnostics.Process.Start()`
- Access `System.Environment` variables
- Use reflection to bypass restrictions

**Approach:**

1. **Import restrictions:** When `AllowDotNetImports` is false, the `from X import Y` statement is a compile error. When it's true, only namespaces in `AllowedNamespaces` are permitted.

2. **Type restrictions:** The `.NET type resolver` checks every resolved type against an allowlist. Blocked types (like `Process`, `Assembly`, `File` when `AllowFileSystem` is false) produce a compile error.

3. **Execution timeout:** Wrap script execution in a `CancellationToken` with a timer. The emitter injects periodic cancellation checks at loop back-edges (every N iterations, check the token). This prevents infinite loops from hanging the host.

4. **Memory limits:** Use `GC.GetTotalMemory()` sampling to approximate script memory usage. Not precise, but good enough to prevent runaway allocation.

**Effort: Medium-High.** Import restriction is easy (compile-time check). Timeout injection at loop edges requires emitter changes. Memory limits are best-effort.

---

## Package Structure

The embedding API ships as a separate NuGet package, keeping the core compiler lean:

```
Culebral.Compiler          # Core compiler (existing) — no runtime dependency
Culebral.Scripting         # Embedding API — depends on Culebral.Compiler
```

```xml
<!-- For applications embedding Culebral -->
<PackageReference Include="Culebral.Scripting" Version="0.2.0" />
```

The `Culebral.Scripting` package references `Culebral.Compiler` as a library (not a CLI tool). The compiler's `Program.Main()` stays as the CLI entry point, while the `Compile` pipeline is reused internally.

---

## Use Cases

### Game Scripting

```csharp
// Unity/Godot/MonoGame — NPC behavior defined in Culebral
var engine = new CulebralEngine(new CulebralEngineOptions {
    ExecutionTimeout = TimeSpan.FromMilliseconds(16), // one frame budget
    AllowDotNetImports = false,  // scripts can't import arbitrary .NET
    AllowFileSystem = false,
    AllowNetwork = false,
});

engine.SetGlobal("self", npc);
engine.SetGlobal("player", player);
engine.SetGlobal("world", world);
engine.SetFunction("move_to", (float x, float y) => npc.MoveTo(x, y));
engine.SetFunction("attack", (Entity target) => npc.Attack(target));
engine.SetFunction("say", (string text) => DialogueSystem.Show(npc, text));

engine.ExecuteFile($"scripts/npcs/{npc.ScriptName}.cbl");
```

```python
# scripts/npcs/guard.cbl
distance = abs(self.position.x - player.position.x)

if distance < 5.0:
    if player.is_hostile:
        attack(player)
        say("Halt, criminal!")
    else:
        say("Stay safe, traveler.")
elif distance < 20.0:
    move_to(player.position.x, player.position.y)
```

### Configuration / Rules Engine

```csharp
// Business rules defined in Culebral, hot-reloadable
var engine = new CulebralEngine();
engine.SetGlobal("order", currentOrder);
engine.SetGlobal("customer", currentCustomer);

decimal discount = engine.Eval<decimal>("""
    base_discount = 0.0
    if customer.tier == "gold":
        base_discount = 0.15
    elif customer.tier == "silver":
        base_discount = 0.10

    if order.total > 500:
        base_discount += 0.05

    min(base_discount, 0.25)  # cap at 25%
""");
```

### Build Tools / Automation

```csharp
// Build pipeline with Culebral scripts
var engine = new CulebralEngine(new CulebralEngineOptions {
    AllowFileSystem = true,
    AllowDotNetImports = true,
    AllowedNamespaces = { "System.IO", "System.Text" },
});

engine.SetFunction("log", (string msg) => Console.WriteLine($"[BUILD] {msg}"));
engine.SetFunction("run", (string cmd) => Process.Start("bash", $"-c \"{cmd}\"").WaitForExit());

engine.ExecuteFile("build.cbl");
```

```python
# build.cbl
from System.IO import Directory, File

log("Cleaning output...")
if Directory.exists("dist"):
    Directory.delete("dist", True)
Directory.create_directory("dist")

log("Compiling...")
run("dotnet build -c Release")

log("Copying artifacts...")
for f in Directory.get_files("bin/Release/net10.0", "*.dll"):
    File.copy(f, f"dist/{File.get_file_name(f)}")

log("Done.")
```

### Template Rendering

```csharp
// Server-side templating with Culebral expressions
var engine = new CulebralEngine();
engine.SetGlobal("user", currentUser);
engine.SetGlobal("items", cartItems);

string greeting = engine.Eval<string>("""
    f"Welcome back, {user.name}! You have {len(items)} items in your cart."
""");
```

### Plugin Systems

```csharp
// Application with user-defined plugins
var engine = new CulebralEngine(new CulebralEngineOptions {
    AllowDotNetImports = false,  // plugins sandboxed
    ExecutionTimeout = TimeSpan.FromSeconds(5),
});

// Expose plugin API — only what you choose
engine.SetFunction("register_command", (string name, Delegate handler) => {
    commandRegistry.Add(name, handler);
});
engine.SetFunction("send_message", (string channel, string text) => {
    chatService.Send(channel, text);
});

// Load user plugins
foreach (var pluginFile in Directory.GetFiles("plugins", "*.cbl"))
{
    try {
        engine.ExecuteFile(pluginFile);
    } catch (CulebralScriptException ex) {
        logger.Warn($"Plugin {pluginFile} failed: {ex.Message}");
    }
}
```

---

## Implementation Roadmap

| Step | What | Effort | Depends on |
|------|------|--------|------------|
| 1 | Extract `CompileFromSource(string)` method | Trivial | Nothing |
| 2 | Add in-memory `AssemblyBuilder` path | Small | Step 1 |
| 3 | Build `CulebralEngine` shell (Execute, Eval) | Small | Step 2 |
| 4 | Global variable injection into symbol table | Medium | Step 3 |
| 5 | Host function registration + invocation | Medium | Step 3 |
| 6 | `print()` output redirection | Small | Step 3 |
| 7 | `CompiledScript` caching | Small | Step 3 |
| 8 | Import sandboxing (allowlist) | Medium | Step 3 |
| 9 | Execution timeout (loop back-edge checks) | Medium | Step 3 |
| 10 | NuGet package (`Culebral.Scripting`) | Small | Steps 1-7 |
| 11 | Documentation + examples | Small | Step 10 |

**Total estimated effort:** Steps 1-7 are the MVP — a working embeddable engine. Steps 8-9 add security. Steps 10-11 are packaging.

The MVP is achievable quickly because the hard part (the compiler) already exists. The embedding layer is mostly plumbing — connecting the existing pipeline to an in-process API instead of a CLI.

---

## Why Culebral Is Uniquely Good at This

Most embeddable scripting languages have a **marshaling problem**. Lua, Python, and JavaScript all run in separate runtimes with separate type systems. Every time the host passes an object to a script or reads a result back, data must be converted, copied, or wrapped in a proxy. This is slow, error-prone, and limits what scripts can do with host objects.

Culebral has none of this. Scripts and host code share the **same .NET runtime**:
- A C# `Player` object passed to a script IS the same object — same memory, same reference, same GC
- Method calls from scripts to host objects are `callvirt` — the same instruction C# uses
- No serialization, no proxy objects, no marshal layer
- Scripts can implement host interfaces and be passed back to C# code that expects those interfaces
- Exceptions propagate naturally across the host/script boundary

This is the same advantage that C# has over Lua in Unity — except Culebral reads like Python, which is dramatically easier for non-programmers (game designers, analysts, ops engineers) to write.

---

## Security Model

Embedding untrusted code is dangerous. The security model is **deny by default, allowlist up**:

| Permission | Default | What it controls |
|---|---|---|
| .NET imports | **Denied** | `from System.X import Y` is a compile error |
| File system | **Denied** | `System.IO` types blocked |
| Network | **Denied** | `System.Net` types blocked |
| Process execution | **Denied** | `System.Diagnostics.Process` blocked |
| Reflection | **Denied** | `System.Reflection` types blocked |
| Environment variables | **Denied** | `System.Environment` blocked |
| Host globals | **Explicit** | Only what the host registers via `SetGlobal` |
| Host functions | **Explicit** | Only what the host registers via `SetFunction` |

The host application explicitly opts in to what scripts can access. Scripts cannot discover or access anything the host hasn't explicitly exposed.

**Execution limits** prevent denial-of-service:
- **Timeout:** Configurable maximum execution time (default 30s). Enforced via cancellation token checks at loop back-edges.
- **Memory:** Approximate memory limit via GC sampling. Not precise, but prevents runaway allocation from crashing the host.
- **Stack depth:** .NET's default stack size (1MB) naturally limits recursion depth. Scripts that overflow get a `StackOverflowException` caught by the host.

---

## API Design Principles

1. **Zero configuration for simple cases.** `new CulebralEngine()` + `engine.Execute(script)` must work with no setup.
2. **Host objects are script objects.** No wrapping, no proxy types, no registration beyond `SetGlobal`.
3. **Errors are exceptions.** `CulebralCompilationException` for syntax/type errors, `CulebralRuntimeException` for script crashes. The host catches them like any other exception.
4. **Thread-safe by default.** Multiple threads can call `Eval` concurrently on the same engine (each gets its own execution context). Shared globals are synchronized.
5. **Deterministic cleanup.** `engine.Dispose()` unloads all compiled assemblies and frees resources. No leaked `AssemblyLoadContext` handles.
