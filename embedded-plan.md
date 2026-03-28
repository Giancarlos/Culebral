# Culebral Embedded Scripting — Architecture Plan

> Design document for rebuilding the CulebralEngine into a production-grade embedding API.
> Written with the rigor of mission-critical infrastructure.

---

## 1. Current State (What's Wrong)

The existing `CulebralEngine` has five fundamental problems:

### 1.1 Out-of-Process Execution

`ExecuteSource()` at `Program.cs:663-706` writes a DLL to a temp directory, then spawns a **child process** via `Process.Start("dotnet", dllPath)`. Every script execution:

- Forks a new OS process (100ms+ overhead)
- Writes to disk (filesystem I/O, temp file cleanup)
- Returns stdout as a string (no structured data)
- Cannot share memory with the host (separate address spaces)

This makes it impossible to:
- Call host functions from scripts at runtime
- Return structured values (objects, collections)
- Execute scripts faster than ~150ms
- Run scripts in tight loops (game tick, request handler)

### 1.2 Source-Level Injection (Regex Hack)

`InjectGlobals()` at `CulebralEngine.cs:94-113` uses regex to find `def` patterns and textually insert variable assignments into source code before compilation. This breaks on:

- Decorators before functions (`@cache\ndef foo():`)
- Multi-line function signatures
- Comments containing `def`
- Nested functions
- Class methods

### 1.3 No Return Values

`Execute()` returns `string` (captured stdout). The only way to get data back is to `print()` it, then parse the string. No way to return ints, lists, objects, or error codes.

### 1.4 No Runtime Host Callbacks

`SetFunction()` pre-computes parameterless functions before compilation and injects their return values as string literals. Scripts cannot call host functions during execution — there is no callback mechanism.

### 1.5 No Isolation or Cleanup

Scripts run in a child process (which provides process-level isolation but at huge cost). There's no mechanism to limit script permissions, execution time, or memory usage.

---

## 2. Target Architecture

### 2.1 Design Principles

1. **In-process execution.** Scripts run inside the host's CLR. No child processes, no temp files, no disk I/O.

2. **Compile once, run many.** Compilation is separated from execution. A compiled script is a reusable artifact.

3. **Shared type system.** Host and script share the .NET type system. No marshaling, no serialization boundaries. A host `List<string>` is the same `List<string>` in the script.

4. **Typed globals.** The host defines a C# class with public fields/properties. The script sees those members as top-level variables. Reading and writing host state is direct field access — zero overhead.

5. **Typed return values.** Scripts return .NET objects, not strings. The API is generic: `Evaluate<int>("2 + 2")` returns `4`, not `"4"`.

6. **Collectible isolation.** Each script execution loads into a collectible `AssemblyLoadContext` that can be fully unloaded, releasing all compiled code and JIT artifacts.

7. **Error handling as data.** Compilation errors are returned as diagnostic collections, not thrown as exceptions. The host decides how to handle them.

### 2.2 API Surface

```csharp
namespace Culebral.Scripting;

// ── Core Types ──

/// Immutable, reusable compiled script. Thread-safe.
public sealed class CulebralScript<TReturn>
{
    /// Compile source code into a reusable script.
    public static CulebralScript<TReturn> Create(
        string code,
        CulebralScriptOptions? options = null);

    /// Get compilation diagnostics (errors, warnings).
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// True if compilation succeeded (no errors).
    public bool IsCompiled { get; }

    /// Execute the script with the given globals. Returns a state snapshot.
    public ScriptState<TReturn> Run(object? globals = null);

    /// Execute async (for scripts with await).
    public Task<ScriptState<TReturn>> RunAsync(object? globals = null);

    /// Create a hot-path delegate for repeated execution.
    /// The delegate accepts a globals object and returns TReturn.
    public Func<object?, TReturn> CreateDelegate();
}

/// Result of a single script execution.
public sealed class ScriptState<TReturn>
{
    /// The script's return value (last expression, or explicit return).
    public TReturn ReturnValue { get; }

    /// All variables defined during execution, with names and values.
    public IReadOnlyList<ScriptVariable> Variables { get; }

    /// Exception thrown during execution, if any.
    public Exception? Exception { get; }

    /// Continue execution with additional code (REPL chaining).
    public ScriptState<TNewReturn> ContinueWith<TNewReturn>(string code);
}

/// A variable captured from script execution.
public sealed record ScriptVariable(string Name, Type Type, object? Value);

/// Immutable compilation options.
public sealed class CulebralScriptOptions
{
    public static CulebralScriptOptions Default { get; }

    /// Add .NET assembly references accessible to the script.
    public CulebralScriptOptions WithReferences(params Assembly[] assemblies);

    /// Add namespace imports (available without 'from X import Y').
    public CulebralScriptOptions WithImports(params string[] namespaces);

    /// Set execution timeout.
    public CulebralScriptOptions WithTimeout(TimeSpan timeout);

    /// Enable/disable file system access.
    public CulebralScriptOptions WithFileSystemAccess(bool allowed);
}

// ── Convenience Entry Points ──

/// Static entry point (mirrors Roslyn's CSharpScript).
public static class CulebralScript
{
    /// One-shot evaluate: compile, run, return value.
    public static TReturn Evaluate<TReturn>(
        string code,
        object? globals = null,
        CulebralScriptOptions? options = null);

    /// One-shot execute: compile, run, return state.
    public static ScriptState<TReturn> Run<TReturn>(
        string code,
        object? globals = null,
        CulebralScriptOptions? options = null);

    /// Compile without executing.
    public static CulebralScript<TReturn> Create<TReturn>(
        string code,
        CulebralScriptOptions? options = null);
}
```

### 2.3 Usage Examples

```csharp
// ── Example 1: Simple evaluation ──
int result = CulebralScript.Evaluate<int>("2 ** 10");
// result == 1024

// ── Example 2: Globals injection ──
var globals = new { name = "Alice", multiplier = 3 };
string greeting = CulebralScript.Evaluate<string>(
    "f'Hello {name}! {multiplier * 7}'",
    globals);
// greeting == "Hello Alice! 21"

// ── Example 3: Compile once, run many ──
var script = CulebralScript.Create<int>("x * x + y");
var fast = script.CreateDelegate();
for (int i = 0; i < 1_000_000; i++)
{
    int val = fast(new { x = i, y = i * 2 });
}

// ── Example 4: Stateful REPL ──
var state = CulebralScript.Run<object>("items = [1, 2, 3]");
state = state.ContinueWith<object>("items.append(4)");
var count = state.ContinueWith<int>("len(items)");
// count.ReturnValue == 4

// ── Example 5: ASP.NET request handler ──
var handler = CulebralScript.Create<string>("""
    response = {"user": user_name, "balance": balance * 1.1}
    return json_encode(response)
""");
app.MapGet("/api/user", (HttpContext ctx) =>
{
    var globals = new { user_name = "Bob", balance = 100.0 };
    return handler.Run(globals).ReturnValue;
});

// ── Example 6: Game scripting ──
var onTick = CulebralScript.Create<object>("""
    if player.health < 20:
        player.speed = player.speed * 0.5
    for enemy in enemies:
        if distance(player, enemy) < 10:
            enemy.attack(player)
""");
// In game loop:
onTick.Run(new { player, enemies, distance = (Func<Entity,Entity,float>)CalcDistance });
```

---

## 3. Implementation Architecture

### 3.1 Compilation Pipeline (In-Memory)

The existing compiler pipeline is:

```
Source → Lexer → Parser → TypeChecker → Lowering → CilEmitter → PersistedAssemblyBuilder → .dll file
```

The change: instead of writing to disk, serialize the assembly to a `byte[]`:

```
Source → Lexer → Parser → TypeChecker → Lowering → CilEmitter → PersistedAssemblyBuilder → byte[]
                                                                                              ↓
                                                            AssemblyLoadContext.LoadFromStream(byte[])
                                                                                              ↓
                                                                                    Assembly (in-memory)
                                                                                              ↓
                                                                           Reflection: find entry point
                                                                                              ↓
                                                                                MethodInfo.Invoke(globals)
```

**Key change in CilEmitter:** Add a `CompileToBytes()` method alongside the existing `Save()` path:

```csharp
// Existing: writes to disk
public void Emit(IrModule module) { ... Save(outputPath); }

// New: returns byte arrays for in-memory loading
public (byte[] Assembly, byte[] Pdb) EmitToMemory(IrModule module);
```

The `PersistedAssemblyBuilder` already supports `GenerateMetadata()` which returns `BlobBuilder` objects. These can be written to `MemoryStream` instead of `FileStream`.

### 3.2 Globals Injection (IR-Level, Not Source-Level)

Instead of regex-injecting variable assignments into source text, inject globals at the IR level:

1. The host passes a `globals` object (anonymous type or concrete class).
2. The compiler reflects on the globals type to discover public fields/properties.
3. During lowering, each global name is registered as a pre-declared variable in the function scope.
4. During CIL emission, global variable loads emit `ldarg.0` (the globals parameter) + `ldfld`/`callvirt get_Property` instead of local variable loads.

**Implementation detail:**

The entry point method signature changes from:

```csharp
// Current: static void Main()
public static void Main() { ... }
```

To:

```csharp
// New: static object Main(object globals)
public static object Main(object globals) { ... }
```

The `globals` parameter is an `object` that the host passes at invocation. Inside the method, global variable access emits:

```
ldarg.0                          // load globals object
castclass <GlobalsType>          // cast to the host-defined type
ldfld <field>                    // load the specific field
```

The type checker sees these as pre-declared variables with known types (resolved via reflection on the globals type).

### 3.3 Return Values

The compiler needs to handle "expression scripts" — scripts whose last statement is an expression (not a function def or assignment). The return value is that expression's value.

**Implementation:**

During lowering, if the last statement in the entry point is an `ExpressionStatement`, convert it to a `ReturnStatement` that returns the expression value. The entry point's return type becomes `object` instead of `void`.

For explicit `return` in top-level code:

```python
# This script returns 42
x = 40
return x + 2
```

The lowering wraps the return value with boxing if needed and the entry point returns `object`.

### 3.4 Collectible AssemblyLoadContext

Each `CulebralScript.Run()` call:

1. Creates a collectible `AssemblyLoadContext`
2. Loads the compiled `byte[]` via `LoadFromStream`
3. Finds the entry point via reflection
4. Invokes with the globals object
5. Captures the return value
6. Unloads the ALC

For hot-path execution (`CreateDelegate()`), the ALC is kept alive and reused. The delegate is cached. Unloading happens when the `CulebralScript` is disposed.

```csharp
internal sealed class ScriptLoadContext : AssemblyLoadContext
{
    public ScriptLoadContext() : base(isCollectible: true) { }

    protected override Assembly? Load(AssemblyName name) => null;
    // Falls back to Default ALC for framework assemblies
}
```

### 3.5 REPL State Chaining

`ContinueWith()` works by:

1. Compiling the new code as a new script
2. The new script's globals include all variables from the previous state
3. The previous state's variables are passed as fields on a synthetic globals object
4. The new script can read and modify them
5. After execution, the new state captures all variables (old + new)

This mirrors Roslyn's submission chaining where each continuation is a new "submission type" that references the previous one.

---

## 4. File-by-File Changes

### 4.1 New Files

| File | Purpose |
|---|---|
| `src/Culebral.Compiler/Scripting/CulebralScript.cs` | Main API: `CulebralScript<T>`, `CulebralScript` static class |
| `src/Culebral.Compiler/Scripting/ScriptState.cs` | `ScriptState<T>`, `ScriptVariable` |
| `src/Culebral.Compiler/Scripting/CulebralScriptOptions.cs` | Immutable options with `With*()` fluent builders |
| `src/Culebral.Compiler/Scripting/ScriptLoadContext.cs` | Collectible ALC for isolation |
| `src/Culebral.Compiler/Scripting/GlobalsBinder.cs` | Reflects on host globals type, generates IR bindings |

### 4.2 Modified Files

| File | Change |
|---|---|
| `CilEmitter.cs` | Add `EmitToMemory()` that returns `(byte[], byte[])` instead of writing to disk |
| `Program.cs` | Add `CompileToMemory()` that returns assembly bytes. Refactor `CompileFromSource` to share pipeline |
| `Lowering.cs` | Support globals parameter injection at IR level. Support expression-as-return-value for scripts |
| `TypeChecker.cs` | Accept pre-declared globals from host type reflection |
| `CulebralEngine.cs` | Rewrite to use new `CulebralScript` API internally (backward compat wrapper) |

### 4.3 Files Removed

None. The old `CulebralEngine` becomes a thin wrapper around the new API for backward compatibility.

---

## 5. Execution Modes

### 5.1 Expression Mode

```csharp
CulebralScript.Evaluate<int>("2 + 2")
```

The source is wrapped as:

```python
def __script__(__globals__: object) -> object:
    return 2 + 2
```

### 5.2 Statement Mode

```csharp
CulebralScript.Run<object>("""
    x = [1, 2, 3]
    x.append(4)
    print(len(x))
""")
```

The source is wrapped as:

```python
def __script__(__globals__: object) -> object:
    x = [1, 2, 3]
    x.append(4)
    print(len(x))
    return None
```

### 5.3 Function Mode

```csharp
CulebralScript.Run<object>("""
    def greet(name: str) -> str:
        return f"Hello, {name}!"

    print(greet("World"))
""")
```

Top-level function definitions are emitted as static methods. The entry point calls them.

---

## 6. Security Considerations

### 6.1 Sandboxing (Future)

The `CulebralScriptOptions.WithFileSystemAccess(false)` flag would be enforced by:

1. Removing `System.IO` from the available namespace imports
2. Not resolving `File`, `Directory`, `Path` types during type checking
3. The collectible ALC ensures no long-lived references to restricted types

Full sandboxing (network, process, reflection) would require a custom `AssemblyLoadContext` that filters type resolution.

### 6.2 Timeouts

`CulebralScriptOptions.WithTimeout(TimeSpan)` would use `CancellationTokenSource` with cooperative cancellation:

1. The script's entry point receives a `CancellationToken` as a hidden parameter
2. Loop lowering inserts `token.ThrowIfCancellationRequested()` at the top of every loop body
3. The host sets a timer that cancels the token after the timeout
4. The script throws `OperationCanceledException` which the API catches and wraps

### 6.3 Memory Limits

Not feasible at the CLR level without process isolation. Documented as a limitation. For memory-sensitive scenarios, recommend running in a child process with OS-level limits (`ulimit`, job objects).

---

## 7. Performance Targets

| Operation | Target | Current |
|---|---|---|
| Cold compile + execute | < 50ms | ~200ms (process fork) |
| Warm execute (cached script) | < 1ms | ~150ms (process fork) |
| Hot-path delegate invoke | < 100ns | N/A |
| Evaluate simple expression | < 10ms | ~200ms |
| ALC load + unload cycle | < 5ms | N/A |

The critical improvement is eliminating the `Process.Start()` overhead. In-memory compilation + `Assembly.Load(byte[])` + `MethodInfo.Invoke()` should achieve sub-millisecond warm execution.

---

## 8. Implementation Order

| Phase | What | Why First |
|---|---|---|
| 1 | `EmitToMemory()` in CilEmitter | Foundation — everything else depends on in-memory assembly bytes |
| 2 | `ScriptLoadContext` + `Assembly.Load` from bytes | In-process execution without disk I/O |
| 3 | `CulebralScript.Evaluate<T>()` one-shot API | Simplest usable API — proves the pipeline works |
| 4 | Globals injection at IR level | Enables host→script data flow without source hacking |
| 5 | Return value extraction | Enables script→host data flow |
| 6 | `CulebralScript<T>.Create()` + `Run()` + `CreateDelegate()` | Full compile-once-run-many API |
| 7 | `ScriptState<T>` + variable capture | REPL chaining and inspection |
| 8 | `CulebralScriptOptions` | Configuration and sandboxing |
| 9 | Timeout support via CancellationToken | Safety for untrusted scripts |
| 10 | Rewrite `CulebralEngine` as wrapper | Backward compatibility |

---

## 9. Testing Strategy

Each phase gets its own test class:

```csharp
public class ScriptEvaluationTests
{
    [Fact] Evaluate_IntExpression_ReturnsInt()
    [Fact] Evaluate_StringExpression_ReturnsString()
    [Fact] Evaluate_WithGlobals_AccessesHostData()
    [Fact] Evaluate_CompilationError_ThrowsWithDiagnostics()
    [Fact] Evaluate_RuntimeError_ThrowsWithStackTrace()
}

public class ScriptCompilationTests
{
    [Fact] Create_ValidCode_IsCompiled()
    [Fact] Create_InvalidCode_HasDiagnostics()
    [Fact] Create_RunMultipleTimes_SameResult()
    [Fact] CreateDelegate_HotPath_SubMicrosecondInvocation()
}

public class ScriptStateTests
{
    [Fact] Run_CapturesVariables()
    [Fact] ContinueWith_PreservesState()
    [Fact] ContinueWith_CanAddNewVariables()
}

public class ScriptIsolationTests
{
    [Fact] Run_UnloadsAssembly_AfterExecution()
    [Fact] Run_ParallelScripts_NoInterference()
    [Fact] Run_Timeout_ThrowsAfterDeadline()
}
```

---

## 10. Open Questions

1. **Lambda closures.** The current compiler doesn't support closures (lambdas can't capture outer variables). This affects the scripting API because expression scripts that reference globals need closure-like access. The globals injection at IR level (Section 3.2) sidesteps this for the scripting case, but the underlying compiler limitation remains for user-written lambdas.

2. **Async scripts.** Should `RunAsync()` support `await` in top-level script code? The compiler already has async state machine support. The question is whether the entry point wrapper should be `async Task<object>` and whether `Evaluate<T>` should await it.

3. **Script-to-host callbacks.** The globals object can include `Func<>` and `Action<>` delegates. The script calls them as regular functions. But the type checker needs to recognize delegate types and infer call signatures. This may require enhancing the type checker to inspect delegate parameter/return types.

4. **Shared assemblies across scripts.** If multiple scripts are compiled against the same host types, should they share a common ALC for those types? Or should each script get a fully independent ALC? Shared types enable faster communication but prevent full isolation.
