using System.Reflection;
using System.Text;

namespace Skelvon.Compiler.Semantics;

/// <summary>
/// Resolves .NET types, methods, properties, and constructors from the BCL
/// and loaded assemblies. Uses System.Reflection at compile time since the
/// compiler runs on the same .NET runtime as the target.
/// </summary>
public sealed class DotNetTypeResolver
{
    private readonly Dictionary<string, Type> _typeCache = new();
    private readonly Dictionary<string, MethodInfo> _methodCache = new();

    /// <summary>
    /// Resolve a fully-qualified .NET type name to a System.Type.
    /// E.g., "System.IO.File" → typeof(System.IO.File)
    /// </summary>
    public Type? ResolveType(string fullName)
    {
        if (_typeCache.TryGetValue(fullName, out var cached))
            return cached;

        // Try direct resolution first
        var type = Type.GetType(fullName);

        // Search all loaded assemblies
        if (type is null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName);
                if (type is not null) break;
            }
        }

        // Try with common assembly qualifiers for BCL types
        if (type is null)
        {
            var assemblies = new[]
            {
                "System.Runtime",
                "System.Console",
                "System.Collections",
                "System.Net.Http",
                "System.IO",
                "System.Linq",
                "System.Threading.Tasks",
                "System.Text.Json",
                "System.Text.RegularExpressions",
                "Microsoft.NETCore.App",
            };

            foreach (var asm in assemblies)
            {
                type = Type.GetType($"{fullName}, {asm}");
                if (type is not null) break;
            }
        }

        if (type is not null)
            _typeCache[fullName] = type;

        return type;
    }

    /// <summary>
    /// Resolve a method on a .NET type by its Skelvon (snake_case) or .NET (PascalCase) name.
    /// Handles overload resolution by argument count.
    /// </summary>
    public MethodInfo? ResolveMethod(Type type, string skelvonName, int argCount, bool isStatic)
    {
        var pascalName = SnakeToPascal(skelvonName);
        var cacheKey = $"{type.FullName}.{pascalName}:{argCount}:{isStatic}";

        if (_methodCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        // Try PascalCase first (the converted name)
        var method = FindMethod(type, pascalName, argCount, flags);

        // Try the original name verbatim (user may have written PascalCase)
        if (method is null && skelvonName != pascalName)
            method = FindMethod(type, skelvonName, argCount, flags);

        if (method is not null)
            _methodCache[cacheKey] = method;

        return method;
    }

    /// <summary>
    /// Resolve a property on a .NET type by its Skelvon or .NET name.
    /// </summary>
    public PropertyInfo? ResolveProperty(Type type, string skelvonName, bool isStatic)
    {
        var pascalName = SnakeToPascal(skelvonName);
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var prop = type.GetProperty(pascalName, flags);
        if (prop is null && skelvonName != pascalName)
            prop = type.GetProperty(skelvonName, flags);

        return prop;
    }

    /// <summary>
    /// Resolve a constructor on a .NET type by argument count.
    /// </summary>
    public ConstructorInfo? ResolveConstructor(Type type, int argCount)
    {
        return type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length == argCount)
            .FirstOrDefault();
    }

    /// <summary>
    /// Resolve a static field on a .NET type.
    /// </summary>
    public FieldInfo? ResolveField(Type type, string skelvonName, bool isStatic)
    {
        var pascalName = SnakeToPascal(skelvonName);
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var field = type.GetField(pascalName, flags);
        if (field is null && skelvonName != pascalName)
            field = type.GetField(skelvonName, flags);

        return field;
    }

    /// <summary>
    /// Convert snake_case to PascalCase.
    /// "read_all_text" → "ReadAllText"
    /// "GetAsync" → "GetAsync" (pass-through)
    /// "get_async" → "GetAsync"
    /// </summary>
    public static string SnakeToPascal(string snake)
    {
        // Already PascalCase: no underscores and starts with uppercase
        if (!snake.Contains('_') && snake.Length > 0 && char.IsUpper(snake[0]))
            return snake;

        // No underscores but starts lowercase: just capitalize first letter
        if (!snake.Contains('_'))
        {
            return char.ToUpper(snake[0]) + snake[1..];
        }

        var sb = new StringBuilder();
        var capitalizeNext = true;

        foreach (var ch in snake)
        {
            if (ch == '_')
            {
                capitalizeNext = true;
                continue;
            }

            sb.Append(capitalizeNext ? char.ToUpper(ch) : ch);
            capitalizeNext = false;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Map a CLR Type back to a SkelvonType for type inference.
    /// </summary>
    public static SkelvonType ClrTypeToSkelvon(Type clrType)
    {
        if (clrType == typeof(void)) return PrimitiveType.Void;
        if (clrType == typeof(int)) return PrimitiveType.Int;
        if (clrType == typeof(long)) return PrimitiveType.Long;
        if (clrType == typeof(double) || clrType == typeof(float)) return PrimitiveType.Float;
        if (clrType == typeof(bool)) return PrimitiveType.Bool;
        if (clrType == typeof(string)) return PrimitiveType.Str;
        if (clrType == typeof(byte)) return PrimitiveType.Byte;
        if (clrType == typeof(char)) return PrimitiveType.Char;
        if (clrType == typeof(object)) return PrimitiveType.Object;
        return new DotNetType(clrType.FullName ?? clrType.Name, clrType);
    }

    private static MethodInfo? FindMethod(Type type, string name, int argCount, BindingFlags flags)
    {
        return type.GetMethods(flags)
            .Where(m => m.Name == name && m.GetParameters().Length == argCount)
            .FirstOrDefault();
    }
}
