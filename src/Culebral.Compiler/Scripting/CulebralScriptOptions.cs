namespace Culebral.Scripting;

/// <summary>
/// Immutable compilation and execution options for Culebral scripts.
/// </summary>
public sealed class CulebralScriptOptions
{
    /// <summary>Default options (no timeout, no restrictions).</summary>
    public static CulebralScriptOptions Default { get; } = new();

    /// <summary>Execution timeout. Null means no timeout.</summary>
    public TimeSpan? Timeout { get; private init; }

    /// <summary>Create new options with the specified execution timeout.</summary>
    public CulebralScriptOptions WithTimeout(TimeSpan timeout) =>
        new() { Timeout = timeout };
}
