namespace Culebral.Compiler.Semantics;

/// <summary>
/// Represents a declared symbol: variable, function, parameter, field, type, etc.
/// </summary>
public sealed class Symbol
{
    public required string Name { get; init; }
    public required SymbolKind Kind { get; init; }
    public required CulebralType Type { get; init; }
    public bool IsMutable { get; init; } = true;
    public bool IsAsync { get; init; }

    /// <summary>For fields: the declaring type name. For params: the function name.</summary>
    public string? DeclaringScope { get; init; }

    public override string ToString() => $"{Kind} {Name}: {Type.DisplayName}";
}

public enum SymbolKind
{
    Variable,
    Parameter,
    Function,
    Field,
    Property,
    Type,
    Module,
    EnumVariant,
    DotNetType,
    DotNetNamespace,
}

/// <summary>
/// Lexically scoped symbol table. Each scope can have a parent scope.
/// Lookup walks up the chain until a match is found or we hit the root.
/// </summary>
public sealed class SymbolScope
{
    private readonly Dictionary<string, Symbol> _symbols = new();
    public SymbolScope? Parent { get; }
    public string ScopeName { get; }

    public SymbolScope(string scopeName, SymbolScope? parent = null)
    {
        ScopeName = scopeName;
        Parent = parent;
    }

    public bool TryDeclare(Symbol symbol)
    {
        return _symbols.TryAdd(symbol.Name, symbol);
    }

    public Symbol? Lookup(string name)
    {
        if (_symbols.TryGetValue(name, out var symbol))
            return symbol;
        return Parent?.Lookup(name);
    }

    public Symbol? LookupLocal(string name)
    {
        _symbols.TryGetValue(name, out var symbol);
        return symbol;
    }

    public SymbolScope CreateChild(string name) => new(name, this);

    public IEnumerable<Symbol> GetLocalSymbols() => _symbols.Values;
}

/// <summary>
/// Root symbol table containing built-in types and functions.
/// </summary>
public static class BuiltinSymbols
{
    public static SymbolScope CreateGlobalScope()
    {
        var scope = new SymbolScope("<global>");

        // Built-in types
        DeclareType(scope, "int", PrimitiveType.Int);
        DeclareType(scope, "long", PrimitiveType.Long);
        DeclareType(scope, "float", PrimitiveType.Float);
        DeclareType(scope, "bool", PrimitiveType.Bool);
        DeclareType(scope, "str", PrimitiveType.Str);
        DeclareType(scope, "byte", PrimitiveType.Byte);
        DeclareType(scope, "char", PrimitiveType.Char);
        DeclareType(scope, "object", PrimitiveType.Object);

        // Built-in functions
        DeclareFunction(scope, "print", [PrimitiveType.Object], PrimitiveType.Void);
        DeclareFunction(scope, "len", [PrimitiveType.Object], PrimitiveType.Int);
        DeclareFunction(scope, "range", [PrimitiveType.Int], PrimitiveType.Object); // simplified
        DeclareFunction(scope, "int", [PrimitiveType.Object], PrimitiveType.Int);
        DeclareFunction(scope, "float", [PrimitiveType.Object], PrimitiveType.Float);
        DeclareFunction(scope, "str", [PrimitiveType.Object], PrimitiveType.Str);
        DeclareFunction(scope, "bool", [PrimitiveType.Object], PrimitiveType.Bool);
        DeclareFunction(scope, "sorted", [PrimitiveType.Object], PrimitiveType.Object);
        DeclareFunction(scope, "abs", [PrimitiveType.Object], PrimitiveType.Object);
        DeclareFunction(scope, "min", [PrimitiveType.Object], PrimitiveType.Object);
        DeclareFunction(scope, "max", [PrimitiveType.Object], PrimitiveType.Object);
        DeclareFunction(scope, "open", [PrimitiveType.Str], PrimitiveType.Object);
        DeclareFunction(scope, "type", [PrimitiveType.Object], PrimitiveType.Str);
        DeclareFunction(scope, "isinstance", [PrimitiveType.Object, PrimitiveType.Object], PrimitiveType.Bool);
        DeclareFunction(scope, "input", [PrimitiveType.Str], PrimitiveType.Str);
        DeclareFunction(scope, "round", [PrimitiveType.Float], PrimitiveType.Int);
        DeclareFunction(scope, "chr", [PrimitiveType.Int], PrimitiveType.Str);
        DeclareFunction(scope, "ord", [PrimitiveType.Str], PrimitiveType.Int);
        DeclareFunction(scope, "enumerate", [PrimitiveType.Object], PrimitiveType.Object);
        DeclareFunction(scope, "zip", [PrimitiveType.Object, PrimitiveType.Object], PrimitiveType.Object);
        DeclareFunction(scope, "map", [PrimitiveType.Object, PrimitiveType.Object], PrimitiveType.Object);
        DeclareFunction(scope, "filter", [PrimitiveType.Object, PrimitiveType.Object], PrimitiveType.Object);

        return scope;
    }

    private static void DeclareType(SymbolScope scope, string name, CulebralType type)
    {
        scope.TryDeclare(new Symbol
        {
            Name = name,
            Kind = SymbolKind.Type,
            Type = type,
            IsMutable = false,
        });
    }

    private static void DeclareFunction(SymbolScope scope, string name, CulebralType[] paramTypes, CulebralType returnType)
    {
        scope.TryDeclare(new Symbol
        {
            Name = name,
            Kind = SymbolKind.Function,
            Type = new FunctionType(paramTypes, returnType),
            IsMutable = false,
        });
    }
}
