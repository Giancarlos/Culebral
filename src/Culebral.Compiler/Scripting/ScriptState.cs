namespace Culebral.Scripting;

/// <summary>
/// A variable captured from script execution.
/// </summary>
public sealed record ScriptVariable(string Name, Type Type, object? Value);
