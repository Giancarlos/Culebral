using System.Reflection;

namespace Culebral.Scripting;

/// <summary>
/// Immutable compilation and execution options for Culebral scripts.
/// Uses copy-on-write fluent builder pattern (all With* methods return new instances).
/// </summary>
public sealed class CulebralScriptOptions
{
    /// <summary>Default options: no timeout, no extra references, no restrictions.</summary>
    public static CulebralScriptOptions Default { get; } = new();

    /// <summary>Execution timeout. Null means no timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Additional .NET assembly references available to the script.</summary>
    public IReadOnlyList<Assembly> References { get; init; } = [];

    /// <summary>Namespace imports automatically available (without 'from X import Y').</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>Whether scripts can access the file system. Default: true.</summary>
    public bool AllowFileSystemAccess { get; init; } = true;

    /// <summary>Whether scripts can access the network. Default: true.</summary>
    public bool AllowNetworkAccess { get; init; } = true;

    /// <summary>Set execution timeout. Scripts that exceed this throw OperationCanceledException.</summary>
    public CulebralScriptOptions WithTimeout(TimeSpan timeout) =>
        new() { Timeout = timeout, References = References, Imports = Imports,
                AllowFileSystemAccess = AllowFileSystemAccess, AllowNetworkAccess = AllowNetworkAccess };

    /// <summary>Add .NET assembly references accessible to the script.</summary>
    public CulebralScriptOptions WithReferences(params Assembly[] assemblies) =>
        new() { Timeout = Timeout, References = [..References, ..assemblies], Imports = Imports,
                AllowFileSystemAccess = AllowFileSystemAccess, AllowNetworkAccess = AllowNetworkAccess };

    /// <summary>Add namespace imports.</summary>
    public CulebralScriptOptions WithImports(params string[] namespaces) =>
        new() { Timeout = Timeout, References = References, Imports = [..Imports, ..namespaces],
                AllowFileSystemAccess = AllowFileSystemAccess, AllowNetworkAccess = AllowNetworkAccess };

    /// <summary>Enable or disable file system access.</summary>
    public CulebralScriptOptions WithFileSystemAccess(bool allowed) =>
        new() { Timeout = Timeout, References = References, Imports = Imports,
                AllowFileSystemAccess = allowed, AllowNetworkAccess = AllowNetworkAccess };

    /// <summary>Enable or disable network access.</summary>
    public CulebralScriptOptions WithNetworkAccess(bool allowed) =>
        new() { Timeout = Timeout, References = References, Imports = Imports,
                AllowFileSystemAccess = AllowFileSystemAccess, AllowNetworkAccess = allowed };
}
