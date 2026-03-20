using System.Reflection;
using System.Text;

namespace Culebral.Compiler.Semantics;

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

        // Try generic type with arity suffixes (e.g., Dictionary → Dictionary`2)
        if (type is null && !fullName.Contains('`'))
        {
            for (var arity = 1; arity <= 8; arity++)
            {
                var genericName = $"{fullName}`{arity}";
                type = Type.GetType(genericName);
                if (type is null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        type = assembly.GetType(genericName);
                        if (type is not null) break;
                    }
                }
                if (type is not null) break;
            }
        }

        if (type is not null)
            _typeCache[fullName] = type;

        return type;
    }

    /// <summary>
    /// Resolve a method on a .NET type by its Culebral (snake_case) or .NET (PascalCase) name.
    /// Handles overload resolution by argument count.
    /// </summary>
    public MethodInfo? ResolveMethod(Type type, string culebralName, int argCount, bool isStatic)
    {
        var pascalName = SnakeToPascal(culebralName);
        var cacheKey = $"{type.FullName}.{pascalName}:{argCount}:{isStatic}";

        if (_methodCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        // Try PascalCase first (the converted name)
        var method = FindMethod(type, pascalName, argCount, flags);

        // Try the original name verbatim (user may have written PascalCase)
        if (method is null && culebralName != pascalName)
            method = FindMethod(type, culebralName, argCount, flags);

        if (method is not null)
            _methodCache[cacheKey] = method;

        return method;
    }

    /// <summary>
    /// Resolve a property on a .NET type by its Culebral or .NET name.
    /// </summary>
    public PropertyInfo? ResolveProperty(Type type, string culebralName, bool isStatic)
    {
        var pascalName = SnakeToPascal(culebralName);
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var prop = type.GetProperty(pascalName, flags);
        if (prop is null && culebralName != pascalName)
            prop = type.GetProperty(culebralName, flags);

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
    public FieldInfo? ResolveField(Type type, string culebralName, bool isStatic)
    {
        var pascalName = SnakeToPascal(culebralName);
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var field = type.GetField(pascalName, flags);
        if (field is null && culebralName != pascalName)
            field = type.GetField(culebralName, flags);

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
    /// Map a CLR Type back to a CulebralType for type inference.
    /// </summary>
    public static CulebralType ClrTypeToCulebral(Type clrType)
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

    /// <summary>
    /// Resolve a generic method on a .NET type by name, argument count, and type argument count.
    /// Returns the open generic MethodInfo — caller must call MakeGenericMethod().
    /// </summary>
    public MethodInfo? ResolveGenericMethod(Type type, string culebralName, int argCount, int typeArgCount, bool isStatic)
    {
        var pascalName = SnakeToPascal(culebralName);
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var method = FindGenericMethod(type, pascalName, argCount, typeArgCount, flags);
        if (method is null && culebralName != pascalName)
            method = FindGenericMethod(type, culebralName, argCount, typeArgCount, flags);

        return method;
    }

    /// <summary>
    /// Search a type for extension methods matching a given receiver type, method name, and arg count.
    /// Extension methods are static methods decorated with [Extension] whose first parameter
    /// is assignable from the receiver type.
    /// </summary>
    public MethodInfo? ResolveExtensionMethod(
        Type extensionSourceType, Type receiverType, string culebralName, int argCount, bool isGeneric = false)
    {
        var pascalName = SnakeToPascal(culebralName);
        var flags = BindingFlags.Public | BindingFlags.Static;

        var method = FindExtensionMethod(extensionSourceType, receiverType, pascalName, argCount, flags, isGeneric);
        if (method is null && culebralName != pascalName)
            method = FindExtensionMethod(extensionSourceType, receiverType, culebralName, argCount, flags, isGeneric);

        return method;
    }

    private static MethodInfo? FindMethod(Type type, string name, int argCount, BindingFlags flags)
    {
        return type.GetMethods(flags)
            .Where(m => m.Name == name && m.GetParameters().Length == argCount)
            .FirstOrDefault();
    }

    private static MethodInfo? FindGenericMethod(Type type, string name, int argCount, int typeArgCount, BindingFlags flags)
    {
        return type.GetMethods(flags)
            .Where(m => m.Name == name
                        && m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == typeArgCount
                        && m.GetParameters().Length == argCount)
            .FirstOrDefault();
    }

    private static MethodInfo? FindExtensionMethod(
        Type extensionSourceType, Type receiverType, string name, int argCount, BindingFlags flags, bool isGeneric)
    {
        // Extension methods are static methods with [Extension] attribute
        // The first parameter is the "this" parameter (receiver type)
        // argCount here is the number of explicit args (not counting the receiver)
        var candidates = extensionSourceType.GetMethods(flags)
            .Where(m => m.Name == name
                        && m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)
                        && m.GetParameters().Length == argCount + 1  // +1 for the receiver
                        && (isGeneric ? m.IsGenericMethodDefinition : !m.IsGenericMethodDefinition));

        foreach (var candidate in candidates)
        {
            var firstParam = candidate.GetParameters()[0].ParameterType;

            // For generic methods, the first param type may be open generic (e.g., IEnumerable<TSource>)
            // We need to check if the receiver type implements/extends the raw generic type
            if (firstParam.IsGenericType && firstParam.ContainsGenericParameters)
            {
                var genericDef = firstParam.GetGenericTypeDefinition();
                if (ImplementsGenericInterface(receiverType, genericDef))
                    return candidate;
            }
            else if (firstParam.IsAssignableFrom(receiverType))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Check if a type implements a generic interface definition (e.g., IEnumerable&lt;&gt;).</summary>
    private static bool ImplementsGenericInterface(Type type, Type genericInterfaceDef)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericInterfaceDef)
            return true;

        return type.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == genericInterfaceDef);
    }
}
