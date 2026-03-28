using System.Reflection;
using System.Runtime.Loader;

namespace Culebral.Scripting;

/// <summary>
/// Collectible AssemblyLoadContext for script isolation.
/// Each script execution loads into its own context that can be fully unloaded.
/// </summary>
internal sealed class ScriptLoadContext : AssemblyLoadContext
{
    public ScriptLoadContext() : base(isCollectible: true) { }

    protected override Assembly? Load(AssemblyName name) => null;
    // Falls back to Default ALC for framework assemblies
}
