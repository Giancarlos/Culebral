namespace Culebral.Compiler.Semantics;

/// <summary>
/// The Culebral type system. Every type in the language is represented by one of these.
/// Designed for efficient comparison and mapping to .NET CLR types.
/// </summary>
public abstract record CulebralType
{
    public abstract string DisplayName { get; }
    public virtual bool IsNullable => false;

    /// <summary>Returns the CLR type this maps to, if resolvable at compile time.</summary>
    public abstract Type? ClrType { get; }
}

// ─── Primitive Types ───

public sealed record PrimitiveType(string Name, Type ClrBackingType) : CulebralType
{
    public override string DisplayName => Name;
    public override Type ClrType => ClrBackingType;

    // Well-known instances
    public static readonly PrimitiveType Int = new("int", typeof(int));
    public static readonly PrimitiveType Long = new("long", typeof(long));
    public static readonly PrimitiveType Float = new("float", typeof(double));
    public static readonly PrimitiveType Bool = new("bool", typeof(bool));
    public static readonly PrimitiveType Str = new("str", typeof(string));
    public static readonly PrimitiveType Byte = new("byte", typeof(byte));
    public static readonly PrimitiveType Char = new("char", typeof(char));
    public static readonly PrimitiveType Void = new("void", typeof(void));
    public static readonly PrimitiveType Object = new("object", typeof(object));
}

// ─── Nullable Wrapper ───

public sealed record NullableCulebralType(CulebralType Inner) : CulebralType
{
    public override string DisplayName => $"{Inner.DisplayName}?";
    public override bool IsNullable => true;
    public override Type? ClrType => Inner.ClrType; // Nullable<T> handled at emit time
}

// ─── Generic Constructed Type ───

public sealed record GenericInstanceType(string Name, CulebralType[] TypeArgs, Type? ClrBackingType) : CulebralType
{
    public override string DisplayName
    {
        get
        {
            var args = string.Join(", ", TypeArgs.Select(t => t.DisplayName));
            return $"{Name}[{args}]";
        }
    }
    public override Type? ClrType => ClrBackingType;
}

// ─── Function Type ───

public sealed record FunctionType(CulebralType[] ParameterTypes, CulebralType ReturnType) : CulebralType
{
    public override string DisplayName
    {
        get
        {
            var paramStr = string.Join(", ", ParameterTypes.Select(p => p.DisplayName));
            return $"({paramStr}) -> {ReturnType.DisplayName}";
        }
    }
    public override Type? ClrType => null; // Functions are not directly a CLR type
}

// ─── Tuple Type ───

public sealed record TupleCulebralType(CulebralType[] Elements, string?[] Names) : CulebralType
{
    public override string DisplayName
    {
        get
        {
            var parts = Elements.Select((e, i) =>
                Names[i] is { } name ? $"{name}: {e.DisplayName}" : e.DisplayName);
            return $"({string.Join(", ", parts)})";
        }
    }
    public override Type? ClrType => null; // Resolved at emit time to ValueTuple<>
}

// ─── User-Defined Types ───

public sealed record ClassType(string Name, string FullyQualifiedName, Type? ClrBackingType = null) : CulebralType
{
    public override string DisplayName => Name;
    public override Type? ClrType => ClrBackingType;
}

public sealed record StructType(string Name, string FullyQualifiedName, Type? ClrBackingType = null) : CulebralType
{
    public override string DisplayName => Name;
    public override Type? ClrType => ClrBackingType;
}

public sealed record RecordType(string Name, string FullyQualifiedName, Type? ClrBackingType = null) : CulebralType
{
    public override string DisplayName => Name;
    public override Type? ClrType => ClrBackingType;
}

public sealed record EnumType(string Name, string FullyQualifiedName) : CulebralType
{
    public override string DisplayName => Name;
    public override Type? ClrType => null; // Sealed class hierarchy
}

public sealed record InterfaceType(string Name, string FullyQualifiedName, Type? ClrBackingType = null) : CulebralType
{
    public override string DisplayName => Name;
    public override Type? ClrType => ClrBackingType;
}

// ─── Type Parameter (Unresolved Generic) ───

public sealed record TypeParameterType(string Name) : CulebralType
{
    public override string DisplayName => Name;
    public override Type? ClrType => null;
}

// ─── .NET Interop Types ───

/// <summary>A .NET type imported via 'from System.IO import File'.</summary>
public sealed record DotNetType(string FullName, Type ClrBackingType) : CulebralType
{
    public override string DisplayName => ClrBackingType.Name;
    public override Type ClrType => ClrBackingType;
}

/// <summary>A .NET namespace alias via 'import System.IO as io'.</summary>
public sealed record DotNetNamespaceType(string Namespace) : CulebralType
{
    public override string DisplayName => Namespace;
    public override Type? ClrType => null;
}

// ─── Error / Unknown ───

public sealed record ErrorType(string Reason) : CulebralType
{
    public override string DisplayName => $"<error: {Reason}>";
    public override Type? ClrType => null;

    public static readonly ErrorType Instance = new("unknown");
}
