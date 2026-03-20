using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.IR;
using Culebral.Compiler.Semantics;

namespace Culebral.Compiler.Emit;

/// <summary>
/// Emits .NET CIL bytecode from CulebralIR using PersistedAssemblyBuilder.
/// Produces a .dll that can be run with `dotnet &lt;output&gt;.dll`.
/// </summary>
public sealed class CilEmitter
{
    private readonly DiagnosticBag _diagnostics;
    private readonly string _outputPath;

    private PersistedAssemblyBuilder _assemblyBuilder = null!;
    private ModuleBuilder _moduleBuilder = null!;

    // Resolved types and methods
    private readonly Dictionary<string, TypeBuilder> _typeBuilders = new();
    private readonly Dictionary<string, MethodBuilder> _methodBuilders = new();
    private readonly Dictionary<string, FieldBuilder> _fieldBuilders = new();
    private readonly Dictionary<string, ConstructorBuilder> _constructorBuilders = new();
    private readonly HashSet<string> _valueTypeNames = new();
    private MethodBuilder? _entryPointMethod;

    public CilEmitter(DiagnosticBag diagnostics, string outputPath)
    {
        _diagnostics = diagnostics;
        _outputPath = outputPath;
    }

    public bool Emit(IrModule module)
    {
        try
        {
            InitializeAssembly(module.Name);
            EmitTypes(module);
            EmitFunctions(module);
            SaveAssembly(module);
            GenerateRuntimeConfig(module.Name);
            return !_diagnostics.HasErrors;
        }
        catch (Exception ex)
        {
            _diagnostics.Error("LEB4000", $"CIL emission failed: {ex.Message}", SourceSpan.None);
            return false;
        }
    }

    private void InitializeAssembly(string name)
    {
        var assemblyName = new AssemblyName(name);
        _assemblyBuilder = new PersistedAssemblyBuilder(assemblyName, typeof(object).Assembly);
        _moduleBuilder = _assemblyBuilder.DefineDynamicModule(name);
    }

    // ─── Type Emission ───

    private void EmitTypes(IrModule module)
    {
        // First pass: define all types (forward references)
        foreach (var typeDef in module.Types)
        {
            var attrs = GetTypeAttributes(typeDef);
            TypeBuilder tb;

            if (typeDef.BaseType is not null && _typeBuilders.TryGetValue(typeDef.BaseType, out var baseType))
            {
                tb = _moduleBuilder.DefineType(typeDef.Name, attrs, baseType);
            }
            else
            {
                Type? parent = typeDef.Kind switch
                {
                    IrTypeKind.Interface => null,
                    IrTypeKind.Struct => typeof(System.ValueType),
                    _ => typeof(object),
                };
                tb = _moduleBuilder.DefineType(typeDef.Name, attrs, parent);

                if (typeDef.Kind == IrTypeKind.Struct)
                    _valueTypeNames.Add(typeDef.Name);
            }

            _typeBuilders[typeDef.Name] = tb;

            // Note: Generic type parameter constraints are carried in IrTypeDef.TypeParameters
            // and enforced at the type-checker level. We do not call DefineGenericParameters here
            // because the compiler currently uses type erasure (T → object) for user-defined generics.
            // Defining CLR-level generic parameters would require rewriting field/method type resolution
            // to use GenericTypeParameterBuilder instead of object. This is tracked for a future phase.
        }

        // Second pass: define fields, constructors, methods, properties
        foreach (var typeDef in module.Types)
        {
            var tb = _typeBuilders[typeDef.Name];

            // Implement interfaces
            foreach (var ifaceName in typeDef.Interfaces)
            {
                if (_typeBuilders.TryGetValue(ifaceName, out var ifaceType))
                    tb.AddInterfaceImplementation(ifaceType);
            }

            // Fields
            foreach (var field in typeDef.Fields)
            {
                var clrType = ResolveClrType(field.Type);
                var fb = tb.DefineField(field.Name, clrType,
                    field.IsStatic ? FieldAttributes.Public | FieldAttributes.Static : FieldAttributes.Public);
                _fieldBuilders[$"{typeDef.Name}.{field.Name}"] = fb;
            }

            // Constructor
            if (typeDef.Constructor is not null && typeDef.Kind is not IrTypeKind.Interface)
            {
                EmitConstructor(tb, typeDef.Constructor, typeDef);
            }
            else if (typeDef.Kind is IrTypeKind.Class or IrTypeKind.SealedClass or IrTypeKind.Record or IrTypeKind.Struct)
            {
                // Generate a default constructor that initializes fields
                EmitDefaultConstructor(tb, typeDef);
            }

            // Methods
            foreach (var method in typeDef.Methods)
            {
                EmitMethod(tb, method, typeDef);
            }

            // Properties
            foreach (var prop in typeDef.Properties)
            {
                EmitProperty(tb, prop, typeDef);
            }
        }

        // Third pass: create types
        foreach (var typeDef in module.Types)
        {
            _typeBuilders[typeDef.Name].CreateType();
        }
    }

    private void EmitConstructor(TypeBuilder tb, IrFunction ctor, IrTypeDef typeDef)
    {
        // Parameter types (excluding 'this' which is implicit in .ctor)
        var paramClrTypes = ctor.Parameters.Select(p => ResolveClrType(p.Type)).ToArray();

        var cb = tb.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            paramClrTypes);

        for (int i = 0; i < ctor.Parameters.Count; i++)
            cb.DefineParameter(i + 1, ParameterAttributes.None, ctor.Parameters[i].Name);

        _constructorBuilders[typeDef.Name] = cb;

        var il = cb.GetILGenerator();

        // Call base constructor (skip for value types — structs don't chain to ValueType ctor)
        if (typeDef.Kind != IrTypeKind.Struct)
        {
            il.Emit(OpCodes.Ldarg_0);
            var baseCtor = (typeDef.BaseType is not null && _typeBuilders.TryGetValue(typeDef.BaseType, out var baseType))
                ? baseType.BaseType?.GetConstructor(Type.EmptyTypes)
                : typeof(object).GetConstructor(Type.EmptyTypes);
            if (baseCtor is not null)
                il.Emit(OpCodes.Call, baseCtor);
        }

        EmitFunctionBody(il, ctor);
    }

    private void EmitDefaultConstructor(TypeBuilder tb, IrTypeDef typeDef)
    {
        var cb = tb.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            Type.EmptyTypes);

        _constructorBuilders[typeDef.Name] = cb;

        var il = cb.GetILGenerator();

        // Call base constructor (skip for value types — structs don't chain to ValueType ctor)
        if (typeDef.Kind != IrTypeKind.Struct)
        {
            il.Emit(OpCodes.Ldarg_0);
            var baseCtor = typeof(object).GetConstructor(Type.EmptyTypes)!;
            il.Emit(OpCodes.Call, baseCtor);
        }

        // Initialize fields with defaults
        foreach (var field in typeDef.Fields)
        {
            var key = $"{typeDef.Name}.{field.Name}";
            if (_fieldBuilders.TryGetValue(key, out var fb) && field.DefaultValue is not null)
            {
                il.Emit(OpCodes.Ldarg_0); // this
                EmitConstantInstruction(il, field.DefaultValue);
                il.Emit(OpCodes.Stfld, fb);
            }
        }

        il.Emit(OpCodes.Ret);
    }

    private static void EmitConstantInstruction(ILGenerator il, IrInstruction instr)
    {
        switch (instr)
        {
            case IrLoadInt { Value: var v } when v >= int.MinValue && v <= int.MaxValue:
                il.Emit(OpCodes.Ldc_I4, (int)v);
                break;
            case IrLoadFloat { Value: var v }:
                il.Emit(OpCodes.Ldc_R8, v);
                break;
            case IrLoadString { Value: var v }:
                il.Emit(OpCodes.Ldstr, v);
                break;
            case IrLoadBool { Value: var v }:
                il.Emit(v ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                break;
            case IrLoadNull:
                il.Emit(OpCodes.Ldnull);
                break;
            default:
                il.Emit(OpCodes.Ldc_I4_0); // Fallback
                break;
        }
    }

    // ─── Dunder method → .NET method mapping ───

    /// <summary>
    /// Maps Python-style dunder method names to .NET operator/override method names.
    /// Returns null if no mapping exists.
    /// </summary>
    private static readonly Dictionary<string, string> DunderToOperator = new()
    {
        ["__eq__"] = "op_Equality",
        ["__ne__"] = "op_Inequality",
        ["__lt__"] = "op_LessThan",
        ["__le__"] = "op_LessThanOrEqual",
        ["__gt__"] = "op_GreaterThan",
        ["__ge__"] = "op_GreaterThanOrEqual",
        ["__add__"] = "op_Addition",
        ["__sub__"] = "op_Subtraction",
        ["__mul__"] = "op_Multiply",
        ["__truediv__"] = "op_Division",
        ["__mod__"] = "op_Modulus",
    };

    /// <summary>
    /// Dunder methods that become virtual overrides of System.Object methods.
    /// </summary>
    private static readonly Dictionary<string, string> DunderToOverride = new()
    {
        ["__str__"] = "ToString",
        ["__repr__"] = "ToString",
        ["__hash__"] = "GetHashCode",
    };

    /// <summary>
    /// Dunder methods that keep their name but are also re-emitted as .NET-friendly methods.
    /// </summary>
    private static readonly Dictionary<string, string> DunderToInstanceMethod = new()
    {
        ["__contains__"] = "Contains",
    };

    private void EmitMethod(TypeBuilder tb, IrFunction method, IrTypeDef typeDef)
    {
        var returnClrType = method.IsGenerator
            ? typeof(System.Collections.IEnumerable)
            : ResolveClrType(method.ReturnType);
        var paramClrTypes = method.Parameters.Select(p => ResolveClrType(p.Type)).ToArray();

        // Check for dunder → override mapping (__str__ → ToString, __repr__ → ToString, __hash__ → GetHashCode)
        if (DunderToOverride.TryGetValue(method.Name, out var overrideName))
        {
            EmitDunderOverride(tb, method, typeDef, overrideName, returnClrType, paramClrTypes);
            return;
        }

        // Check for dunder → operator mapping (__add__ → op_Addition, __eq__ → op_Equality, etc.)
        if (DunderToOperator.TryGetValue(method.Name, out var operatorName))
        {
            EmitDunderOperator(tb, method, typeDef, operatorName, returnClrType, paramClrTypes);

            // __eq__ also generates an Equals(object) override
            if (method.Name == "__eq__")
                EmitEqualsOverride(tb, method, typeDef);

            return;
        }

        // Check for __contains__ → also emit as Contains
        if (DunderToInstanceMethod.TryGetValue(method.Name, out var instanceName))
        {
            EmitDunderInstanceMethod(tb, method, typeDef, instanceName, returnClrType, paramClrTypes);
            return;
        }

        // Regular method emission
        var emitName = method.Name;

        var methodAttrs = MethodAttributes.Public | MethodAttributes.HideBySig;
        bool isInterfaceDefaultMethod = false;
        if (method.IsStatic)
            methodAttrs |= MethodAttributes.Static;
        if (typeDef.Kind == IrTypeKind.Interface)
        {
            // Check if this interface method has a default implementation body
            // An abstract method has exactly one basic block with a single IrReturn(false)
            var totalInstructions = method.Body.Sum(b => b.Instructions.Count);
            var isAbstract = totalInstructions <= 1
                && method.Body.All(b => b.Instructions.All(i => i is IrReturn { HasValue: false }));
            if (isAbstract)
                methodAttrs |= MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot;
            else
            {
                methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
                isInterfaceDefaultMethod = true;
            }
        }
        else if (!method.IsStatic && typeDef.Interfaces.Count > 0)
            methodAttrs |= MethodAttributes.Virtual; // For interface implementation

        var mb = tb.DefineMethod(emitName, methodAttrs, returnClrType, paramClrTypes);

        // Name parameters
        for (int i = 0; i < method.Parameters.Count; i++)
            mb.DefineParameter(i + 1, ParameterAttributes.None, method.Parameters[i].Name);

        _methodBuilders[$"{typeDef.Name}.{emitName}"] = mb;

        // Apply decorator attributes
        ApplyDecoratorAttributes(mb, method);

        // Don't emit body for abstract interface methods (but do emit for default implementations)
        if (typeDef.Kind == IrTypeKind.Interface && !isInterfaceDefaultMethod)
            return;

        var il = mb.GetILGenerator();
        EmitFunctionBody(il, method);
    }

    /// <summary>
    /// Emits a dunder method as a virtual override (e.g. __str__ → ToString, __hash__ → GetHashCode).
    /// </summary>
    private void EmitDunderOverride(TypeBuilder tb, IrFunction method, IrTypeDef typeDef,
        string overrideName, Type returnClrType, Type[] paramClrTypes)
    {
        var methodAttrs = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual;

        // For GetHashCode, force return type to int
        var actualReturnType = overrideName == "GetHashCode" ? typeof(int) : returnClrType;

        var mb = tb.DefineMethod(overrideName, methodAttrs, actualReturnType, paramClrTypes);

        for (int i = 0; i < method.Parameters.Count; i++)
            mb.DefineParameter(i + 1, ParameterAttributes.None, method.Parameters[i].Name);

        _methodBuilders[$"{typeDef.Name}.{overrideName}"] = mb;
        _methodBuilders[$"{typeDef.Name}.{method.Name}"] = mb;

        if (typeDef.Kind == IrTypeKind.Interface) return;

        var il = mb.GetILGenerator();
        EmitFunctionBody(il, method);
    }

    /// <summary>
    /// Emits a dunder method as a static operator method (e.g. __add__ → op_Addition).
    /// The user's instance method taking one 'other' parameter becomes a static method taking (T left, T right).
    /// The body calls the original dunder logic: left.__add__(right).
    /// </summary>
    private void EmitDunderOperator(TypeBuilder tb, IrFunction method, IrTypeDef typeDef,
        string operatorName, Type returnClrType, Type[] paramClrTypes)
    {
        // First, emit the original dunder method as an instance method (so it can be called directly)
        var instanceAttrs = MethodAttributes.Public | MethodAttributes.HideBySig;
        var instanceMb = tb.DefineMethod(method.Name, instanceAttrs, returnClrType, paramClrTypes);
        for (int i = 0; i < method.Parameters.Count; i++)
            instanceMb.DefineParameter(i + 1, ParameterAttributes.None, method.Parameters[i].Name);
        _methodBuilders[$"{typeDef.Name}.{method.Name}"] = instanceMb;

        if (typeDef.Kind != IrTypeKind.Interface)
        {
            var instanceIl = instanceMb.GetILGenerator();
            EmitFunctionBody(instanceIl, method);
        }

        // Now emit the static operator method: op_XXX(T, T) → calls left.__dunder__(right)
        var opAttrs = MethodAttributes.Public | MethodAttributes.Static |
                      MethodAttributes.SpecialName | MethodAttributes.HideBySig;

        // Operator takes two params of the declaring type, returns the method's return type
        var opParamTypes = new[] { tb, tb };
        var opMb = tb.DefineMethod(operatorName, opAttrs, returnClrType, opParamTypes);
        opMb.DefineParameter(1, ParameterAttributes.None, "left");
        opMb.DefineParameter(2, ParameterAttributes.None, "right");
        _methodBuilders[$"{typeDef.Name}.{operatorName}"] = opMb;

        if (typeDef.Kind != IrTypeKind.Interface)
        {
            var opIl = opMb.GetILGenerator();
            // Load left (arg 0), load right (arg 1), call instance method on left
            opIl.Emit(OpCodes.Ldarg_0); // left
            opIl.Emit(OpCodes.Ldarg_1); // right
            opIl.Emit(OpCodes.Call, instanceMb);
            opIl.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// Emits an Equals(object) override for __eq__, which casts and delegates to __eq__.
    /// </summary>
    private void EmitEqualsOverride(TypeBuilder tb, IrFunction method, IrTypeDef typeDef)
    {
        var equalsAttrs = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual;
        var equalsMb = tb.DefineMethod("Equals", equalsAttrs, typeof(bool), [typeof(object)]);
        equalsMb.DefineParameter(1, ParameterAttributes.None, "obj");
        _methodBuilders[$"{typeDef.Name}.Equals"] = equalsMb;

        if (typeDef.Kind == IrTypeKind.Interface) return;

        var il = equalsMb.GetILGenerator();
        var falseLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // if (obj is T other) return this.__eq__(other); else return false;
        il.Emit(OpCodes.Ldarg_1); // obj
        il.Emit(OpCodes.Isinst, tb);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, falseLabel);

        // obj is our type — call __eq__
        var otherLocal = il.DeclareLocal(tb);
        il.Emit(OpCodes.Stloc, otherLocal);
        il.Emit(OpCodes.Ldarg_0); // this
        il.Emit(OpCodes.Ldloc, otherLocal); // other (already cast)

        // Call the instance __eq__ method
        if (_methodBuilders.TryGetValue($"{typeDef.Name}.__eq__", out var eqMb))
            il.Emit(OpCodes.Call, eqMb);
        else
            il.Emit(OpCodes.Ceq); // Fallback

        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(falseLabel);
        il.Emit(OpCodes.Pop); // pop the null from isinst
        il.Emit(OpCodes.Ldc_I4_0); // false

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a dunder method both under its original name AND under a .NET-friendly name
    /// (e.g. __contains__ as both __contains__ and Contains).
    /// </summary>
    private void EmitDunderInstanceMethod(TypeBuilder tb, IrFunction method, IrTypeDef typeDef,
        string netName, Type returnClrType, Type[] paramClrTypes)
    {
        // Emit the original dunder method
        var instanceAttrs = MethodAttributes.Public | MethodAttributes.HideBySig;
        var instanceMb = tb.DefineMethod(method.Name, instanceAttrs, returnClrType, paramClrTypes);
        for (int i = 0; i < method.Parameters.Count; i++)
            instanceMb.DefineParameter(i + 1, ParameterAttributes.None, method.Parameters[i].Name);
        _methodBuilders[$"{typeDef.Name}.{method.Name}"] = instanceMb;

        if (typeDef.Kind != IrTypeKind.Interface)
        {
            var il = instanceMb.GetILGenerator();
            EmitFunctionBody(il, method);
        }

        // Emit the .NET-friendly alias that delegates to the dunder method
        var aliasMb = tb.DefineMethod(netName, instanceAttrs, returnClrType, paramClrTypes);
        for (int i = 0; i < method.Parameters.Count; i++)
            aliasMb.DefineParameter(i + 1, ParameterAttributes.None, method.Parameters[i].Name);
        _methodBuilders[$"{typeDef.Name}.{netName}"] = aliasMb;

        if (typeDef.Kind != IrTypeKind.Interface)
        {
            var aliasIl = aliasMb.GetILGenerator();
            aliasIl.Emit(OpCodes.Ldarg_0); // this
            for (int i = 0; i < method.Parameters.Count; i++)
                EmitLoadArg(aliasIl, i + 1); // +1 because arg 0 is this
            aliasIl.Emit(OpCodes.Call, instanceMb);
            aliasIl.Emit(OpCodes.Ret);
        }
    }

    private void EmitProperty(TypeBuilder tb, IrProperty prop, IrTypeDef typeDef)
    {
        var clrType = ResolveClrType(prop.Type);
        var pb = tb.DefineProperty(prop.Name, PropertyAttributes.None, clrType, Type.EmptyTypes);

        if (prop.Getter is not null)
        {
            var getterAttrs = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
            // Make property getters virtual on abstract classes (for polymorphic dispatch)
            if (typeDef.Kind == IrTypeKind.AbstractClass)
                getterAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
            // Make property getters virtual overrides on sealed subclasses that inherit from a base type
            else if (typeDef.Kind == IrTypeKind.SealedClass && typeDef.BaseType is not null
                     && _typeBuilders.ContainsKey(typeDef.BaseType))
                getterAttrs |= MethodAttributes.Virtual;

            var getterMb = tb.DefineMethod($"get_{prop.Name}",
                getterAttrs, clrType, Type.EmptyTypes);
            _methodBuilders[$"{typeDef.Name}.get_{prop.Name}"] = getterMb;
            var il = getterMb.GetILGenerator();
            EmitFunctionBody(il, prop.Getter);
            pb.SetGetMethod(getterMb);
        }

        if (prop.Setter is not null)
        {
            var setterAttrs = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
            if (typeDef.Kind == IrTypeKind.AbstractClass)
                setterAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
            else if (typeDef.Kind == IrTypeKind.SealedClass && typeDef.BaseType is not null
                     && _typeBuilders.ContainsKey(typeDef.BaseType))
                setterAttrs |= MethodAttributes.Virtual;

            var setterMb = tb.DefineMethod($"set_{prop.Name}",
                setterAttrs, typeof(void), [clrType]);
            setterMb.DefineParameter(1, ParameterAttributes.None, "value");
            _methodBuilders[$"{typeDef.Name}.set_{prop.Name}"] = setterMb;
            var il = setterMb.GetILGenerator();
            EmitFunctionBody(il, prop.Setter);
            pb.SetSetMethod(setterMb);
        }
    }

    // ─── Function Emission ───

    private void EmitFunctions(IrModule module)
    {
        // Create a Program class for top-level functions
        var programType = _moduleBuilder.DefineType("Program",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
        _typeBuilders["Program"] = programType;

        // Pass 1: Define all method signatures (enables forward references / mutual recursion)
        var functionsToEmit = new List<(IrFunction Func, MethodBuilder Builder)>();
        foreach (var func in module.Functions)
        {
            if (func.DeclaringType is not null)
                continue;

            var returnClrType = func.IsGenerator
                ? typeof(System.Collections.IEnumerable)
                : ResolveClrType(func.ReturnType);
            var paramClrTypes = func.Parameters.Select(p => ResolveClrType(p.Type)).ToArray();

            var methodAttrs = MethodAttributes.Public | MethodAttributes.Static;
            var mb = programType.DefineMethod(
                func.IsEntryPoint ? "Main" : func.Name,
                methodAttrs, returnClrType, paramClrTypes);

            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var paramAttrs = func.Parameters[i].IsVarArgs ? ParameterAttributes.None : ParameterAttributes.None;
                var pb = mb.DefineParameter(i + 1, paramAttrs, func.Parameters[i].Name);
                if (func.Parameters[i].IsVarArgs)
                    pb.SetCustomAttribute(new CustomAttributeBuilder(
                        typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes)!, []));
            }

            _methodBuilders[func.Name] = mb;
            functionsToEmit.Add((func, mb));

            // Apply decorator attributes
            ApplyDecoratorAttributes(mb, func);

            if (func.IsEntryPoint)
                _entryPointMethod = mb;
        }

        // Pass 2: Emit all method bodies (all methods are now resolvable)
        foreach (var (func, mb) in functionsToEmit)
        {
            var il = mb.GetILGenerator();
            EmitFunctionBody(il, func);
        }

        programType.CreateType();
    }

    /// <summary>
    /// Applies .NET custom attributes to a method from decorator metadata.
    /// If a decorator name resolves to a .NET type inheriting from System.Attribute,
    /// emits a .custom attribute on the method.
    /// </summary>
    private void ApplyDecoratorAttributes(MethodBuilder mb, IrFunction func)
    {
        if (func.Decorators is null || func.Decorators.Count == 0)
            return;

        foreach (var decoratorName in func.Decorators)
        {
            // Skip the 'native' decorator — it's handled separately
            if (decoratorName == "native")
                continue;

            // Try to resolve as a .NET attribute type
            var attrType = TryResolveAttributeType(decoratorName);
            if (attrType is not null)
            {
                var ctor = attrType.GetConstructor(Type.EmptyTypes);
                if (ctor is not null)
                {
                    var cab = new CustomAttributeBuilder(ctor, []);
                    mb.SetCustomAttribute(cab);
                }
            }
        }
    }

    /// <summary>
    /// Attempts to resolve a decorator name to a .NET attribute type.
    /// Tries the name as-is, with "Attribute" suffix, and in the System namespace.
    /// </summary>
    private static Type? TryResolveAttributeType(string name)
    {
        // Try exact name, then with "Attribute" suffix
        var candidates = new[]
        {
            name,
            name + "Attribute",
            "System." + name,
            "System." + name + "Attribute",
            "System.Runtime.CompilerServices." + name,
            "System.Runtime.CompilerServices." + name + "Attribute",
            "System.Diagnostics." + name,
            "System.Diagnostics." + name + "Attribute",
            "System.ComponentModel." + name,
            "System.ComponentModel." + name + "Attribute",
        };

        foreach (var candidate in candidates)
        {
            var type = Type.GetType(candidate);
            if (type is not null && typeof(Attribute).IsAssignableFrom(type))
                return type;
        }

        // Search all loaded assemblies
        foreach (var candidate in candidates)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(candidate);
                    if (type is not null && typeof(Attribute).IsAssignableFrom(type))
                        return type;
                }
                catch { /* ignore assembly load errors */ }
            }
        }

        return null;
    }

    private void EmitFunctionBody(ILGenerator il, IrFunction func)
    {
        // Declare locals
        var locals = new LocalBuilder[func.Locals.Count];
        for (int i = 0; i < func.Locals.Count; i++)
        {
            var clrType = ResolveClrType(func.Locals[i].Type);
            locals[i] = il.DeclareLocal(clrType);
        }

        // For generator functions, create a List<object> local to collect yielded values
        LocalBuilder? generatorListLocal = null;
        if (func.IsGenerator)
        {
            generatorListLocal = il.DeclareLocal(typeof(List<object>));
            il.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Stloc, generatorListLocal);
        }

        // Create labels for all basic blocks
        var labels = new Dictionary<string, Label>();
        foreach (var block in func.Body)
        {
            labels[block.Label] = il.DefineLabel();
        }

        // Emit basic blocks
        foreach (var block in func.Body)
        {
            il.MarkLabel(labels[block.Label]);

            foreach (var instr in block.Instructions)
            {
                EmitInstruction(il, instr, locals, labels, func, generatorListLocal);
            }
        }
    }

    private void EmitInstruction(ILGenerator il, IrInstruction instr,
        LocalBuilder[] locals, Dictionary<string, Label> labels, IrFunction func,
        LocalBuilder? generatorListLocal = null)
    {
        switch (instr)
        {
            case IrLoadInt { Value: var v } when v >= -1 && v <= 8:
                il.Emit(v switch
                {
                    -1 => OpCodes.Ldc_I4_M1,
                    0 => OpCodes.Ldc_I4_0,
                    1 => OpCodes.Ldc_I4_1,
                    2 => OpCodes.Ldc_I4_2,
                    3 => OpCodes.Ldc_I4_3,
                    4 => OpCodes.Ldc_I4_4,
                    5 => OpCodes.Ldc_I4_5,
                    6 => OpCodes.Ldc_I4_6,
                    7 => OpCodes.Ldc_I4_7,
                    8 => OpCodes.Ldc_I4_8,
                    _ => OpCodes.Ldc_I4_0,
                });
                break;

            case IrLoadInt { Value: var v } when v >= int.MinValue && v <= int.MaxValue:
                il.Emit(OpCodes.Ldc_I4, (int)v);
                break;

            case IrLoadInt { Value: var v }:
                il.Emit(OpCodes.Ldc_I8, v);
                break;

            case IrLoadFloat { Value: var v }:
                il.Emit(OpCodes.Ldc_R8, v);
                break;

            case IrLoadString { Value: var v }:
                il.Emit(OpCodes.Ldstr, v);
                break;

            case IrLoadBool { Value: var v }:
                il.Emit(v ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                break;

            case IrLoadNull:
                il.Emit(OpCodes.Ldnull);
                break;

            case IrLoadLocal { Index: var idx }:
                EmitLoadLocal(il, idx, locals);
                break;

            case IrStoreLocal { Index: var idx }:
                EmitStoreLocal(il, idx, locals);
                break;

            case IrLoadArg { Index: var idx }:
                EmitLoadArg(il, idx);
                break;

            case IrLoadThis:
                il.Emit(OpCodes.Ldarg_0);
                break;

            case IrBinaryOp { Op: var op, OperandType: var opType }:
                EmitBinaryOp(il, op, opType);
                break;

            case IrUnaryOp { Op: var op }:
                EmitUnaryOp(il, op);
                break;

            case IrBranch { TargetLabel: var target }:
                if (labels.TryGetValue(target, out var targetLabel))
                    il.Emit(OpCodes.Br, targetLabel);
                break;

            case IrBranchIf { TrueLabel: var trueLabel, FalseLabel: var falseLabel }:
                if (labels.TryGetValue(trueLabel, out var tl))
                    il.Emit(OpCodes.Brtrue, tl);
                if (labels.TryGetValue(falseLabel, out var fl))
                    il.Emit(OpCodes.Br, fl);
                break;

            case IrReturn { HasValue: false }:
                if (generatorListLocal is not null)
                    il.Emit(OpCodes.Ldloc, generatorListLocal);
                il.Emit(OpCodes.Ret);
                break;

            case IrReturn { HasValue: true }:
                if (generatorListLocal is not null)
                {
                    // In a generator, explicit "return value" is unusual — pop the value,
                    // return the collected list instead
                    il.Emit(OpCodes.Pop);
                    il.Emit(OpCodes.Ldloc, generatorListLocal);
                }
                il.Emit(OpCodes.Ret);
                break;

            case IrYield:
                if (generatorListLocal is not null)
                {
                    // Stack has: [value]
                    // Store value to a temp, load list, load temp, call Add
                    var yieldTemp = il.DeclareLocal(typeof(object));
                    il.Emit(OpCodes.Stloc, yieldTemp);
                    il.Emit(OpCodes.Ldloc, generatorListLocal);
                    il.Emit(OpCodes.Ldloc, yieldTemp);
                    il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add")!);
                }
                else
                {
                    // yield outside generator — just pop the value
                    il.Emit(OpCodes.Pop);
                }
                break;

            case IrPrint printInstr:
                EmitPrint(il, printInstr, func, locals);
                break;

            case IrCallBuiltin callBuiltin:
                EmitBuiltinCall(il, callBuiltin, func, locals);
                break;

            case IrCall { FunctionName: var name, ArgCount: var argc, IsStatic: true }:
                EmitStaticCall(il, name, argc);
                break;

            case IrCallVirtual cv:
                EmitVirtualCall(il, cv.MethodName, cv.ArgCount, cv, func);
                break;

            case IrCallMethod { DeclaringType: var dt, MethodName: var mn, ArgCount: var ac }:
                EmitMethodCall(il, dt, mn, ac);
                break;

            case IrNewObj newObj:
                EmitNewObj(il, newObj.TypeName, newObj.ArgCount, newObj, func);
                break;

            case IrBox { Type: var type }:
                var boxType = ResolveClrType(type);
                if (boxType.IsValueType)
                    il.Emit(OpCodes.Box, boxType);
                break;

            case IrUnbox { Type: var type }:
                var unboxType = ResolveClrType(type);
                il.Emit(OpCodes.Unbox_Any, unboxType);
                break;

            case IrToString { SourceType: var sourceType }:
                EmitToString(il, sourceType);
                break;

            case IrStringConcat { PartCount: var count }:
                EmitStringConcat(il, count);
                break;

            case IrDup:
                il.Emit(OpCodes.Dup);
                break;

            case IrPop:
                il.Emit(OpCodes.Pop);
                break;

            case IrNop:
                il.Emit(OpCodes.Nop);
                break;

            case IrLoadField { DeclaringType: var dt, FieldName: var fname }:
            {
                var key = $"{dt}.{fname}";
                if (_fieldBuilders.TryGetValue(key, out var loadFb))
                    il.Emit(OpCodes.Ldfld, loadFb);
                break;
            }

            case IrStoreField { DeclaringType: var dt, FieldName: var fname }:
            {
                var key = $"{dt}.{fname}";
                if (_fieldBuilders.TryGetValue(key, out var storeFb))
                {
                    // Auto-box value types when storing to object fields (type erasure)
                    if (storeFb.FieldType == typeof(object))
                    {
                        var valueType = InferStackTopType(instr, func);
                        if (valueType.IsValueType)
                            il.Emit(OpCodes.Box, valueType);
                    }
                    il.Emit(OpCodes.Stfld, storeFb);
                }
                break;
            }

            case IrCastClass { TypeName: var name }:
                EmitCast(il, name);
                break;

            case IrIsInst { TypeName: var name }:
                EmitIsInst(il, name);
                break;

            case IrIsNull { Negated: var negated }:
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ceq);
                if (negated)
                {
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ceq);
                }
                break;

            case IrThrow:
                il.Emit(OpCodes.Throw);
                break;

            case IrBeginExceptionBlock:
                il.BeginExceptionBlock();
                break;

            case IrBeginCatchBlock { ExceptionType: var excType }:
                il.BeginCatchBlock(excType);
                break;

            case IrBeginFinallyBlock:
                il.BeginFinallyBlock();
                break;

            case IrEndExceptionBlock:
                il.EndExceptionBlock();
                break;

            // ─── .NET Interop ───

            case IrCallDotNetStatic { DeclaringType: var type, MethodName: var mname, ArgCount: var argc }:
            {
                var method = FindDotNetMethod(type, mname, argc, isStatic: true);
                if (method is not null)
                    il.Emit(OpCodes.Call, method);
                else
                    _diagnostics.Error("LEB4010", $"Cannot resolve .NET method {type.Name}.{mname}", instr.Span);
                break;
            }

            case IrCallDotNetInstance { DeclaringType: var type, MethodName: var mname, ArgCount: var argc }:
            {
                var method = FindDotNetMethod(type, mname, argc, isStatic: false);
                if (method is not null)
                    il.Emit(OpCodes.Callvirt, method);
                else
                    _diagnostics.Error("LEB4011", $"Cannot resolve .NET instance method {type.Name}.{mname}", instr.Span);
                break;
            }

            case IrLoadDotNetProperty { DeclaringType: var type, PropertyName: var pname, IsStatic: var isStatic }:
            {
                var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
                var prop = type.GetProperty(pname, flags);
                if (prop?.GetGetMethod() is { } getter)
                    il.Emit(isStatic ? OpCodes.Call : OpCodes.Callvirt, getter);
                else
                    _diagnostics.Error("LEB4012", $"Cannot resolve .NET property {type.Name}.{pname}", instr.Span);
                break;
            }

            case IrLoadDotNetField { DeclaringType: var type, FieldName: var fname, IsStatic: var isStatic }:
            {
                var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
                var field = type.GetField(fname, flags);
                if (field is not null)
                    il.Emit(isStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, field);
                else
                    _diagnostics.Error("LEB4013", $"Cannot resolve .NET field {type.Name}.{fname}", instr.Span);
                break;
            }

            case IrNewDotNetObj { Type: var type, ArgCount: var argc }:
            {
                var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(c => c.GetParameters().Length == argc);
                if (ctor is not null)
                    il.Emit(OpCodes.Newobj, ctor);
                else
                    _diagnostics.Error("LEB4014", $"Cannot resolve .NET constructor for {type.Name} with {argc} args", instr.Span);
                break;
            }

            // ─── Generic .NET Method Calls ───

            case IrCallDotNetGenericStatic { DeclaringType: var type, MethodName: var mname, ArgCount: var argc, TypeArguments: var typeArgs }:
            {
                var method = FindDotNetGenericMethod(type, mname, argc, typeArgs.Length, isStatic: true);
                if (method is not null)
                {
                    var closed = method.MakeGenericMethod(typeArgs);
                    il.Emit(OpCodes.Call, closed);
                }
                else
                    _diagnostics.Error("LEB4015", $"Cannot resolve generic .NET method {type.Name}.{mname}<{string.Join(", ", typeArgs.Select(t => t.Name))}>", instr.Span);
                break;
            }

            case IrCallDotNetGenericInstance { DeclaringType: var type, MethodName: var mname, ArgCount: var argc, TypeArguments: var typeArgs }:
            {
                var method = FindDotNetGenericMethod(type, mname, argc, typeArgs.Length, isStatic: false);
                if (method is not null)
                {
                    var closed = method.MakeGenericMethod(typeArgs);
                    il.Emit(OpCodes.Callvirt, closed);
                }
                else
                    _diagnostics.Error("LEB4016", $"Cannot resolve generic .NET instance method {type.Name}.{mname}<{string.Join(", ", typeArgs.Select(t => t.Name))}>", instr.Span);
                break;
            }

            // ─── Extension Method Calls ───

            case IrCallExtensionMethod { DeclaringType: var extType, MethodName: var mname, ArgCount: var argc, TypeArguments: var typeArgs }:
            {
                var method = FindExtensionMethod(extType, mname, argc, typeArgs);
                if (method is not null)
                {
                    if (typeArgs is not null && method.IsGenericMethodDefinition)
                        method = method.MakeGenericMethod(typeArgs);
                    il.Emit(OpCodes.Call, method);
                    // Auto-box value type returns so they can be stored in object locals / printed
                    if (method.ReturnType.IsValueType && method.ReturnType != typeof(void))
                        il.Emit(OpCodes.Box, method.ReturnType);
                }
                else
                    _diagnostics.Error("LEB4017", $"Cannot resolve extension method {extType.Name}.{mname}", instr.Span);
                break;
            }

            // ─── Lambda Delegate Creation ───

            case IrCreateDelegate { MethodName: var mname, ParamCount: var paramCount }:
            {
                if (_methodBuilders.TryGetValue(mname, out var lambdaMb))
                {
                    // Build the appropriate Func<> delegate type
                    // For N params: Func<object, object, ..., object> (N+1 object type args)
                    var funcTypeArgs = Enumerable.Repeat(typeof(object), paramCount + 1).ToArray();
                    var delegateType = paramCount switch
                    {
                        0 => typeof(Func<object>),
                        1 => typeof(Func<,>).MakeGenericType(funcTypeArgs),
                        2 => typeof(Func<,,>).MakeGenericType(funcTypeArgs),
                        3 => typeof(Func<,,,>).MakeGenericType(funcTypeArgs),
                        4 => typeof(Func<,,,,>).MakeGenericType(funcTypeArgs),
                        _ => typeof(Delegate), // fallback
                    };

                    var delegateCtor = delegateType.GetConstructor([typeof(object), typeof(nint)])!;
                    il.Emit(OpCodes.Ldnull); // null target (static method)
                    il.Emit(OpCodes.Ldftn, lambdaMb);
                    il.Emit(OpCodes.Newobj, delegateCtor);
                }
                else
                {
                    _diagnostics.Warning("LEB4020", $"Cannot resolve lambda method '{mname}'", instr.Span);
                    il.Emit(OpCodes.Ldnull);
                }
                break;
            }

            // ─── Delegate Invocation ───

            case IrInvokeDelegate { ArgCount: var dArgCount }:
            {
                // Stack: [delegate, arg0, arg1, ..., argN]
                // We need to box value-type args and invoke through the appropriate Func<> type.
                // Build the Func<object, ..., object> type matching the arg count.
                var funcTypeArgs = Enumerable.Repeat(typeof(object), dArgCount + 1).ToArray();
                var delegateType = dArgCount switch
                {
                    0 => typeof(Func<object>),
                    1 => typeof(Func<,>).MakeGenericType(funcTypeArgs),
                    2 => typeof(Func<,,>).MakeGenericType(funcTypeArgs),
                    3 => typeof(Func<,,,>).MakeGenericType(funcTypeArgs),
                    4 => typeof(Func<,,,,>).MakeGenericType(funcTypeArgs),
                    _ => typeof(Delegate), // fallback
                };

                // Args are already on the stack after the delegate.
                // We need to reorder: save args to temps, cast delegate, reload args, then call Invoke.
                var argTemps = new LocalBuilder[dArgCount];
                for (int i = dArgCount - 1; i >= 0; i--)
                {
                    argTemps[i] = il.DeclareLocal(typeof(object));
                    il.Emit(OpCodes.Stloc, argTemps[i]);
                }

                // Cast the delegate reference on the stack to the concrete Func<> type
                il.Emit(OpCodes.Castclass, delegateType);

                // Reload args
                for (int i = 0; i < dArgCount; i++)
                    il.Emit(OpCodes.Ldloc, argTemps[i]);

                // Call Invoke on the delegate
                var invokeMethod = delegateType.GetMethod("Invoke")!;
                il.Emit(OpCodes.Callvirt, invokeMethod);
                break;
            }

            // ─── Slicing ───

            case IrSlice { HasStart: var hasStart, HasStop: var hasStop, HasStep: var hasStep }:
            {
                // Stack: [object, start (int), stop (int), step (int)]
                // Call the runtime helper: CulebralSlice(object, int, int, int, bool, bool) -> object
                EmitSliceHelper(il);
                break;
            }

            // ─── List concatenation ───

            case IrListConcat:
            {
                // Stack: [left_list (List<object>), right_list (List<object>)]
                // → new List<object>(left_list) then AddRange(right_list)
                var rightTemp = il.DeclareLocal(typeof(List<object>));
                il.Emit(OpCodes.Castclass, typeof(List<object>));
                il.Emit(OpCodes.Stloc, rightTemp);
                il.Emit(OpCodes.Castclass, typeof(System.Collections.Generic.IEnumerable<object>));
                il.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor([typeof(System.Collections.Generic.IEnumerable<object>)])!);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldloc, rightTemp);
                il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("AddRange")!);
                break;
            }

            // ─── List repetition ───

            case IrListRepeat:
            {
                // Stack: [list (List<object>), count (int)]
                // → new List<object>(), loop count times calling AddRange
                var countTemp = il.DeclareLocal(typeof(int));
                var srcTemp = il.DeclareLocal(typeof(List<object>));
                il.Emit(OpCodes.Stloc, countTemp);
                il.Emit(OpCodes.Castclass, typeof(List<object>));
                il.Emit(OpCodes.Stloc, srcTemp);
                il.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                // Loop: i = 0; while (i < count) { result.AddRange(src); i++; }
                var loopIdx = il.DeclareLocal(typeof(int));
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, loopIdx);
                var loopCheck = il.DefineLabel();
                var loopBody = il.DefineLabel();
                il.Emit(OpCodes.Br, loopCheck);
                il.MarkLabel(loopBody);
                il.Emit(OpCodes.Dup); // dup result list
                il.Emit(OpCodes.Ldloc, srcTemp);
                il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("AddRange")!);
                il.Emit(OpCodes.Ldloc, loopIdx);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, loopIdx);
                il.MarkLabel(loopCheck);
                il.Emit(OpCodes.Ldloc, loopIdx);
                il.Emit(OpCodes.Ldloc, countTemp);
                il.Emit(OpCodes.Blt, loopBody);
                // result list remains on stack
                break;
            }

            // ─── String repetition ───

            case IrStringRepeat:
            {
                // Stack: [string, int_count]
                // → new StringBuilder(str.Length * count).Insert(0, str, count).ToString()
                var countTemp = il.DeclareLocal(typeof(int));
                var strTemp = il.DeclareLocal(typeof(string));
                il.Emit(OpCodes.Stloc, countTemp);
                il.Emit(OpCodes.Stloc, strTemp);
                // str.Length * count → capacity
                il.Emit(OpCodes.Ldloc, strTemp);
                il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length")!.GetGetMethod()!);
                il.Emit(OpCodes.Ldloc, countTemp);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Newobj, typeof(System.Text.StringBuilder).GetConstructor([typeof(int)])!);
                // .Insert(0, str, count)
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldloc, strTemp);
                il.Emit(OpCodes.Ldloc, countTemp);
                il.Emit(OpCodes.Callvirt, typeof(System.Text.StringBuilder).GetMethod("Insert", [typeof(int), typeof(string), typeof(int)])!);
                // .ToString()
                il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString")!);
                break;
            }

            // ─── Array from stack (for varargs) ───

            case IrNewArrayFromStack { Count: var count }:
            {
                // Pop `count` values from stack into a new object[]
                // Save them to temp locals (reverse order), then build array
                var tempLocals = new LocalBuilder[count];
                for (int i = count - 1; i >= 0; i--)
                {
                    tempLocals[i] = il.DeclareLocal(typeof(object));
                    il.Emit(OpCodes.Stloc, tempLocals[i]);
                }

                il.Emit(OpCodes.Ldc_I4, count);
                il.Emit(OpCodes.Newarr, typeof(object));

                for (int i = 0; i < count; i++)
                {
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldloc, tempLocals[i]);
                    il.Emit(OpCodes.Stelem_Ref);
                }
                break;
            }

            case IrLoadElement:
            {
                // Stack: [collection (object), index (int)]
                // Adjust negative index: if index < 0, index += collection.Count
                // Then call IList.get_Item(int)
                var idxLocal = il.DeclareLocal(typeof(int));
                var colLocal = il.DeclareLocal(typeof(object));
                il.Emit(OpCodes.Stloc, idxLocal);   // pop index
                il.Emit(OpCodes.Stloc, colLocal);   // pop collection

                // Negative index adjustment
                var skipAdjust = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, idxLocal);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Bge, skipAdjust);
                // index < 0: index += collection.Count
                il.Emit(OpCodes.Ldloc, colLocal);
                il.Emit(OpCodes.Castclass, typeof(System.Collections.ICollection));
                il.Emit(OpCodes.Callvirt, typeof(System.Collections.ICollection).GetProperty("Count")!.GetGetMethod()!);
                il.Emit(OpCodes.Ldloc, idxLocal);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, idxLocal);
                il.MarkLabel(skipAdjust);

                // Call IList[index]
                il.Emit(OpCodes.Ldloc, colLocal);
                il.Emit(OpCodes.Castclass, typeof(System.Collections.IList));
                il.Emit(OpCodes.Ldloc, idxLocal);
                il.Emit(OpCodes.Callvirt, typeof(System.Collections.IList).GetMethod("get_Item", [typeof(int)])!);
                break;
            }

            default:
                il.Emit(OpCodes.Nop);
                break;
        }
    }

    // ─── Specific Emission Helpers ───

    private static void EmitLoadLocal(ILGenerator il, int index, LocalBuilder[] locals)
    {
        if (index >= 0 && index < locals.Length)
        {
            switch (index)
            {
                case 0: il.Emit(OpCodes.Ldloc_0); break;
                case 1: il.Emit(OpCodes.Ldloc_1); break;
                case 2: il.Emit(OpCodes.Ldloc_2); break;
                case 3: il.Emit(OpCodes.Ldloc_3); break;
                default: il.Emit(OpCodes.Ldloc, locals[index]); break;
            }
        }
    }

    private static void EmitStoreLocal(ILGenerator il, int index, LocalBuilder[] locals)
    {
        if (index >= 0 && index < locals.Length)
        {
            switch (index)
            {
                case 0: il.Emit(OpCodes.Stloc_0); break;
                case 1: il.Emit(OpCodes.Stloc_1); break;
                case 2: il.Emit(OpCodes.Stloc_2); break;
                case 3: il.Emit(OpCodes.Stloc_3); break;
                default: il.Emit(OpCodes.Stloc, locals[index]); break;
            }
        }
    }

    private static void EmitLoadArg(ILGenerator il, int index)
    {
        switch (index)
        {
            case 0: il.Emit(OpCodes.Ldarg_0); break;
            case 1: il.Emit(OpCodes.Ldarg_1); break;
            case 2: il.Emit(OpCodes.Ldarg_2); break;
            case 3: il.Emit(OpCodes.Ldarg_3); break;
            default: il.Emit(OpCodes.Ldarg, index); break;
        }
    }

    private static void EmitBinaryOp(ILGenerator il, IrBinaryOpKind op, CulebralType? operandType = null)
    {
        // String concatenation
        if (op == IrBinaryOpKind.Add && operandType == PrimitiveType.Str)
        {
            var concatMethod = typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!;
            il.Emit(OpCodes.Call, concatMethod);
            return;
        }

        switch (op)
        {
            case IrBinaryOpKind.Add: il.Emit(OpCodes.Add); break;
            case IrBinaryOpKind.Sub: il.Emit(OpCodes.Sub); break;
            case IrBinaryOpKind.Mul: il.Emit(OpCodes.Mul); break;
            case IrBinaryOpKind.Div:
                // True division: convert int operands to double for Python-style semantics
                if (operandType is PrimitiveType pt && pt == PrimitiveType.Int)
                {
                    // Stack: [left (int), right (int)] → save right, conv left, reload right, conv right
                    var divTmp = il.DeclareLocal(typeof(int));
                    il.Emit(OpCodes.Stloc, divTmp);   // save right
                    il.Emit(OpCodes.Conv_R8);          // convert left to double
                    il.Emit(OpCodes.Ldloc, divTmp);    // reload right
                    il.Emit(OpCodes.Conv_R8);          // convert right to double
                }
                il.Emit(OpCodes.Div);
                break;
            case IrBinaryOpKind.Mod: il.Emit(OpCodes.Rem); break;
            case IrBinaryOpKind.BitAnd: il.Emit(OpCodes.And); break;
            case IrBinaryOpKind.BitOr: il.Emit(OpCodes.Or); break;
            case IrBinaryOpKind.BitXor: il.Emit(OpCodes.Xor); break;
            case IrBinaryOpKind.ShiftLeft: il.Emit(OpCodes.Shl); break;
            case IrBinaryOpKind.ShiftRight: il.Emit(OpCodes.Shr); break;
            case IrBinaryOpKind.Equal: il.Emit(OpCodes.Ceq); break;
            case IrBinaryOpKind.LessThan: il.Emit(OpCodes.Clt); break;
            case IrBinaryOpKind.GreaterThan: il.Emit(OpCodes.Cgt); break;
            case IrBinaryOpKind.NotEqual:
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case IrBinaryOpKind.LessEqual:
                il.Emit(OpCodes.Cgt);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case IrBinaryOpKind.GreaterEqual:
                il.Emit(OpCodes.Clt);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
            case IrBinaryOpKind.IntDiv:
                il.Emit(OpCodes.Div);
                break;
            case IrBinaryOpKind.Pow:
            {
                // Math.Pow(a, b) — both args must be double
                // Stack has: a, b. Save b, convert a, reload b, convert b
                var powTmp = il.DeclareLocal(typeof(int));
                il.Emit(OpCodes.Stloc, powTmp);  // save b
                il.Emit(OpCodes.Conv_R8);         // convert a to double
                il.Emit(OpCodes.Ldloc, powTmp);   // reload b
                il.Emit(OpCodes.Conv_R8);         // convert b to double
                var mathPow = typeof(Math).GetMethod("Pow", [typeof(double), typeof(double)])!;
                il.Emit(OpCodes.Call, mathPow);
                break;
            }
            case IrBinaryOpKind.LogicalAnd:
                il.Emit(OpCodes.And);
                break;
            case IrBinaryOpKind.LogicalOr:
                il.Emit(OpCodes.Or);
                break;
        }
    }

    private static void EmitUnaryOp(ILGenerator il, IrUnaryOpKind op)
    {
        switch (op)
        {
            case IrUnaryOpKind.Negate: il.Emit(OpCodes.Neg); break;
            case IrUnaryOpKind.BitNot: il.Emit(OpCodes.Not); break;
            case IrUnaryOpKind.LogicalNot:
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                break;
        }
    }

    private void EmitPrint(ILGenerator il, IrPrint print, IrFunction func, LocalBuilder[] locals)
    {
        var argc = print.PositionalArgCount;
        var sep = print.Sep;
        var end = print.End;
        var flush = print.Flush;
        var useStderr = print.UseStderr;

        // ── Fast path: single arg, default sep/end, no flush, stdout ──
        if (argc == 1 && sep is null && end is null && !flush && !useStderr)
        {
            var stackType = InferStackTopType(print, func);
            if (stackType == typeof(int))
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(int)])!);
            else if (stackType == typeof(double))
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(double)])!);
            else if (stackType == typeof(string))
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(string)])!);
            else if (stackType == typeof(bool))
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(bool)])!);
            else
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(object)])!);
            return;
        }

        // ── No-args: just print newline (or custom end) ──
        if (argc == 0)
        {
            if (useStderr)
                il.Emit(OpCodes.Call, typeof(Console).GetProperty("Error")!.GetGetMethod()!);

            var effectiveEnd = end ?? "\n";
            if (effectiveEnd == "\n")
            {
                if (useStderr)
                    il.Emit(OpCodes.Callvirt, typeof(System.IO.TextWriter).GetMethod("WriteLine", Type.EmptyTypes)!);
                else
                    il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", Type.EmptyTypes)!);
            }
            else
            {
                il.Emit(OpCodes.Ldstr, effectiveEnd);
                if (useStderr)
                    il.Emit(OpCodes.Callvirt, typeof(System.IO.TextWriter).GetMethod("Write", [typeof(string)])!);
                else
                    il.Emit(OpCodes.Call, typeof(Console).GetMethod("Write", [typeof(string)])!);
            }

            if (flush)
            {
                if (useStderr)
                {
                    il.Emit(OpCodes.Call, typeof(Console).GetProperty("Error")!.GetGetMethod()!);
                    il.Emit(OpCodes.Callvirt, typeof(System.IO.TextWriter).GetMethod("Flush")!);
                }
                else
                {
                    il.Emit(OpCodes.Call, typeof(Console).GetProperty("Out")!.GetGetMethod()!);
                    il.Emit(OpCodes.Callvirt, typeof(System.IO.TextWriter).GetMethod("Flush")!);
                }
            }
            return;
        }

        // ── General case: multiple args or named args ──
        // Stack currently has [arg0, arg1, ..., argN-1] (N = argc)
        // We need to collect them into a string[] by calling ToString on each.

        // Create a local object[] to hold the args (they're already on the stack)
        var arrLocal = il.DeclareLocal(typeof(object[]));
        // We need to store them in reverse order since they're on the stack
        il.Emit(OpCodes.Ldc_I4, argc);
        il.Emit(OpCodes.Newarr, typeof(object));
        il.Emit(OpCodes.Stloc, arrLocal);

        // The values are on the stack in order: arg0 is deepest, argN-1 is on top.
        // We need to pop them in reverse order (top first = last arg).
        // Use temp locals to hold them, then store into the array.
        var tempLocals = new LocalBuilder[argc];
        for (int i = 0; i < argc; i++)
            tempLocals[i] = il.DeclareLocal(typeof(object));

        // Pop from stack into temps (reverse order)
        for (int i = argc - 1; i >= 0; i--)
        {
            // Box value types before storing to object local
            var argType = InferNthArgType(print, func, i, argc);
            if (argType.IsValueType)
                il.Emit(OpCodes.Box, argType);
            il.Emit(OpCodes.Stloc, tempLocals[i]);
        }

        // Store temps into the array in forward order
        for (int i = 0; i < argc; i++)
        {
            il.Emit(OpCodes.Ldloc, arrLocal);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldloc, tempLocals[i]);
            il.Emit(OpCodes.Stelem_Ref);
        }

        // Convert each element to string: create string[] and fill with ToString calls
        var strArrLocal = il.DeclareLocal(typeof(string[]));
        il.Emit(OpCodes.Ldc_I4, argc);
        il.Emit(OpCodes.Newarr, typeof(string));
        il.Emit(OpCodes.Stloc, strArrLocal);

        for (int i = 0; i < argc; i++)
        {
            il.Emit(OpCodes.Ldloc, strArrLocal);
            il.Emit(OpCodes.Ldc_I4, i);
            // Load from object array, call ToString
            il.Emit(OpCodes.Ldloc, arrLocal);
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ldelem_Ref);
            // Python prints "True"/"False" for bools — handle via object.ToString()
            il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString", Type.EmptyTypes)!);
            il.Emit(OpCodes.Stelem_Ref);
        }

        // String.Join(sep, stringArray)
        il.Emit(OpCodes.Ldstr, sep ?? " ");
        il.Emit(OpCodes.Ldloc, strArrLocal);
        il.Emit(OpCodes.Call, typeof(string).GetMethod("Join", [typeof(string), typeof(string[])])!);

        // Now the joined string is on the stack.
        // Determine writer and write.
        var effectiveEndGeneral = end ?? "\n";

        if (useStderr)
        {
            // Store joined string, get writer, load string, write
            var joinedLocal = il.DeclareLocal(typeof(string));
            il.Emit(OpCodes.Stloc, joinedLocal);
            il.Emit(OpCodes.Call, typeof(Console).GetProperty("Error")!.GetGetMethod()!);
            il.Emit(OpCodes.Ldloc, joinedLocal);

            if (effectiveEndGeneral == "\n")
            {
                il.Emit(OpCodes.Callvirt, typeof(System.IO.TextWriter).GetMethod("WriteLine", [typeof(string)])!);
            }
            else if (effectiveEndGeneral == "")
            {
                il.Emit(OpCodes.Callvirt, typeof(System.IO.TextWriter).GetMethod("Write", [typeof(string)])!);
            }
            else
            {
                il.Emit(OpCodes.Callvirt, typeof(System.IO.TextWriter).GetMethod("Write", [typeof(string)])!);
                il.Emit(OpCodes.Call, typeof(Console).GetProperty("Error")!.GetGetMethod()!);
                il.Emit(OpCodes.Ldstr, effectiveEndGeneral);
                il.Emit(OpCodes.Callvirt, typeof(System.IO.TextWriter).GetMethod("Write", [typeof(string)])!);
            }
        }
        else
        {
            // stdout path
            if (effectiveEndGeneral == "\n")
            {
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(string)])!);
            }
            else if (effectiveEndGeneral == "")
            {
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("Write", [typeof(string)])!);
            }
            else
            {
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("Write", [typeof(string)])!);
                il.Emit(OpCodes.Ldstr, effectiveEndGeneral);
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("Write", [typeof(string)])!);
            }
        }

        if (flush)
        {
            if (useStderr)
                il.Emit(OpCodes.Call, typeof(Console).GetProperty("Error")!.GetGetMethod()!);
            else
                il.Emit(OpCodes.Call, typeof(Console).GetProperty("Out")!.GetGetMethod()!);
            il.Emit(OpCodes.Callvirt, typeof(System.IO.TextWriter).GetMethod("Flush")!);
        }
    }

    private void EmitBuiltinCall(ILGenerator il, IrCallBuiltin callBuiltin, IrFunction func, LocalBuilder[] locals)
    {
        var name = callBuiltin.Name;
        var argc = callBuiltin.ArgCount;

        switch (name)
        {
            case "print":
            {
                // Determine the type on the stack by looking at what instruction produced it
                var stackType = InferStackTopType(callBuiltin, func);
                if (stackType == typeof(int))
                {
                    il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(int)])!);
                }
                else if (stackType == typeof(double))
                {
                    il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(double)])!);
                }
                else if (stackType == typeof(string))
                {
                    il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(string)])!);
                }
                else if (stackType == typeof(bool))
                {
                    il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(bool)])!);
                }
                else
                {
                    il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(object)])!);
                }
                break;
            }

            case "len":
            {
                // Determine the type on the stack to call the right property
                var lenTargetType = InferStackTopType(callBuiltin, func);
                if (lenTargetType == typeof(string))
                {
                    // string.Length
                    il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length")!.GetGetMethod()!);
                }
                else if (lenTargetType.IsArray)
                {
                    // array.Length
                    il.Emit(OpCodes.Ldlen);
                    il.Emit(OpCodes.Conv_I4);
                }
                else if (lenTargetType == typeof(HashSet<object>))
                {
                    // HashSet<object>.Count — HashSet doesn't implement non-generic ICollection
                    il.Emit(OpCodes.Callvirt,
                        typeof(HashSet<object>).GetProperty("Count")!.GetGetMethod()!);
                }
                else if (lenTargetType == typeof(Dictionary<object, object>))
                {
                    // Dictionary<object, object>.Count
                    il.Emit(OpCodes.Callvirt,
                        typeof(Dictionary<object, object>).GetProperty("Count")!.GetGetMethod()!);
                }
                else if (lenTargetType is TypeBuilder lenTb &&
                         _methodBuilders.TryGetValue($"{lenTb.Name}.__len__", out var lenMethod))
                {
                    // User-defined type with __len__ method
                    il.Emit(OpCodes.Call, lenMethod);
                }
                else
                {
                    // For List<> and other ICollection types — cast to ICollection first
                    // so the IL verifier accepts the callvirt when the stack type is object
                    il.Emit(OpCodes.Castclass, typeof(System.Collections.ICollection));
                    il.Emit(OpCodes.Callvirt,
                        typeof(System.Collections.ICollection).GetProperty("Count")!.GetGetMethod()!);
                }
                break;
            }

            case "range":
            {
                var enumRange = typeof(Enumerable).GetMethod("Range", [typeof(int), typeof(int)])!;
                if (argc == 1)
                {
                    // range(n) → Enumerable.Range(0, n)
                    var tmpLocal = il.DeclareLocal(typeof(int));
                    il.Emit(OpCodes.Stloc, tmpLocal);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ldloc, tmpLocal);
                    il.Emit(OpCodes.Call, enumRange);
                }
                else if (argc == 2)
                {
                    // range(start, stop) → Enumerable.Range(start, stop - start)
                    var stopLocal = il.DeclareLocal(typeof(int));
                    var startLocal = il.DeclareLocal(typeof(int));
                    il.Emit(OpCodes.Stloc, stopLocal);  // pop stop
                    il.Emit(OpCodes.Stloc, startLocal);  // pop start
                    il.Emit(OpCodes.Ldloc, startLocal);
                    il.Emit(OpCodes.Ldloc, stopLocal);
                    il.Emit(OpCodes.Ldloc, startLocal);
                    il.Emit(OpCodes.Sub);                 // stop - start = count
                    il.Emit(OpCodes.Call, enumRange);
                }
                else
                {
                    // range(start, stop, step) — custom loop supporting negative step
                    EmitRangeWithStep(il);
                }
                break;
            }

            case "int":
                if (argc == 2)
                {
                    // int(s, base) → Convert.ToInt32(string, int)
                    var convertWithBase = typeof(Convert).GetMethod("ToInt32", [typeof(string), typeof(int)])!;
                    il.Emit(OpCodes.Call, convertWithBase);
                }
                else
                {
                    var convertToInt = typeof(Convert).GetMethod("ToInt32", [typeof(object)])!;
                    il.Emit(OpCodes.Call, convertToInt);
                }
                break;

            case "float":
                var convertToDouble = typeof(Convert).GetMethod("ToDouble", [typeof(object)])!;
                il.Emit(OpCodes.Call, convertToDouble);
                break;

            case "str":
                var objToString = typeof(object).GetMethod("ToString", Type.EmptyTypes)!;
                il.Emit(OpCodes.Callvirt, objToString);
                break;

            case "abs":
            {
                var absArgType = InferStackTopType(callBuiltin, func);
                if (absArgType == typeof(int))
                {
                    il.Emit(OpCodes.Call, typeof(Math).GetMethod("Abs", [typeof(int)])!);
                }
                else
                {
                    il.Emit(OpCodes.Call, typeof(Math).GetMethod("Abs", [typeof(double)])!);
                }
                break;
            }

            case "min":
            {
                if (argc == 1)
                {
                    // min(iterable) → iterate and find minimum using Comparer<object>.Default
                    EmitIterableMinMax(il, isMin: true);
                }
                else
                {
                    var minArgType = InferStackTopType(callBuiltin, func);
                    if (minArgType == typeof(double))
                    {
                        il.Emit(OpCodes.Call, typeof(Math).GetMethod("Min", [typeof(double), typeof(double)])!);
                    }
                    else
                    {
                        il.Emit(OpCodes.Call, typeof(Math).GetMethod("Min", [typeof(int), typeof(int)])!);
                    }
                }
                break;
            }

            case "max":
            {
                if (argc == 1)
                {
                    // max(iterable) → iterate and find maximum using Comparer<object>.Default
                    EmitIterableMinMax(il, isMin: false);
                }
                else
                {
                    var maxArgType = InferStackTopType(callBuiltin, func);
                    if (maxArgType == typeof(double))
                    {
                        il.Emit(OpCodes.Call, typeof(Math).GetMethod("Max", [typeof(double), typeof(double)])!);
                    }
                    else
                    {
                        il.Emit(OpCodes.Call, typeof(Math).GetMethod("Max", [typeof(int), typeof(int)])!);
                    }
                }
                break;
            }

            case "input":
            {
                // input(prompt) → Console.Write(prompt); Console.ReadLine()
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("Write", [typeof(string)])!);
                il.Emit(OpCodes.Call, typeof(Console).GetMethod("ReadLine", Type.EmptyTypes)!);
                break;
            }

            case "round":
            {
                if (argc == 2)
                {
                    // round(x, ndigits) → Math.Round(double, int) → returns double
                    il.Emit(OpCodes.Call, typeof(Math).GetMethod("Round", [typeof(double), typeof(int)])!);
                }
                else
                {
                    // round(x) → (int)Math.Round(x)
                    il.Emit(OpCodes.Call, typeof(Math).GetMethod("Round", [typeof(double)])!);
                    il.Emit(OpCodes.Conv_I4);
                }
                break;
            }

            case "chr":
            {
                // chr(n) → ((char)n).ToString()
                il.Emit(OpCodes.Conv_U2); // convert int to char (unsigned 16-bit)
                var charLocal = il.DeclareLocal(typeof(char));
                il.Emit(OpCodes.Stloc, charLocal);
                il.Emit(OpCodes.Ldloca, charLocal);
                il.Emit(OpCodes.Call, typeof(char).GetMethod("ToString", Type.EmptyTypes)!);
                break;
            }

            case "ord":
            {
                // ord(c) → c[0] cast to int
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("get_Chars", [typeof(int)])!);
                il.Emit(OpCodes.Conv_I4);
                break;
            }

            case "type":
            {
                // type(x) → x.GetType().Name
                var typeArgType = InferStackTopType(callBuiltin, func);
                if (typeArgType.IsValueType)
                    il.Emit(OpCodes.Box, typeArgType);
                il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetType")!);
                il.Emit(OpCodes.Callvirt, typeof(Type).GetProperty("Name")!.GetGetMethod()!);
                break;
            }

            case "bool":
            {
                // bool(x) — truthiness conversion
                var boolArgType = InferStackTopType(callBuiltin, func);
                if (boolArgType == typeof(bool))
                {
                    // already a bool, nothing to do
                }
                else if (boolArgType == typeof(int))
                {
                    // int != 0
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Cgt_Un); // pushes 1 if nonzero, 0 if zero
                }
                else if (boolArgType == typeof(string))
                {
                    // !string.IsNullOrEmpty(s)
                    il.Emit(OpCodes.Call, typeof(string).GetMethod("IsNullOrEmpty", [typeof(string)])!);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ceq); // negate
                }
                else
                {
                    // For reference types: null → false, ICollection with Count==0 → false, else → true
                    if (boolArgType.IsValueType)
                        il.Emit(OpCodes.Box, boolArgType);
                    EmitObjectTruthiness(il);
                }
                break;
            }

            case "sorted":
            {
                // sorted(iterable) → new List<object>(IEnumerable), .Sort(), return list
                EmitSortedHelper(il);
                break;
            }

            case "reversed":
            {
                // reversed(iterable) → new List<object>(IEnumerable), .Reverse(), return list
                EmitReversedHelper(il);
                break;
            }

            case "enumerate":
            {
                // enumerate(iterable) → list of (int, object) tuples via helper
                EmitEnumerateHelper(il);
                break;
            }

            case "zip":
            {
                // zip(a, b) → list of (object, object) tuples via helper
                EmitZipHelper(il);
                break;
            }

            case "map":
            {
                // map(fn, iterable) → list of fn(x) for x in iterable via helper
                EmitMapHelper(il);
                break;
            }

            case "filter":
            {
                // filter(fn, iterable) → list of x for x in iterable if fn(x) via helper
                EmitFilterHelper(il);
                break;
            }

            case "isinstance":
            {
                // isinstance(x, T) — runtime type check
                // Stack: [x, typeName]. We need both as object for the helper.
                // Save typeName, box x if needed, then push typeName back.
                var typeNameTmp = il.DeclareLocal(typeof(object));
                il.Emit(OpCodes.Stloc, typeNameTmp); // pop typeName
                // Now x is on top. Find the type of the first arg (instruction i-2).
                var isinstArgType = InferNthArgType(callBuiltin, func, 0, 2);
                if (isinstArgType.IsValueType)
                    il.Emit(OpCodes.Box, isinstArgType);
                il.Emit(OpCodes.Ldloc, typeNameTmp); // push typeName back
                EmitIsinstanceHelper(il);
                break;
            }

            case "all":
            {
                // all(iterable) → true if all elements are truthy
                EmitAllHelper(il);
                break;
            }

            case "any":
            {
                // any(iterable) → true if any element is truthy
                EmitAnyHelper(il);
                break;
            }

            case "sum":
            {
                // sum(iterable) → sum of all elements
                EmitSumHelper(il);
                break;
            }

            case "list":
            {
                // list(iterable) → new List<object>(IEnumerable)
                if (argc == 0)
                {
                    il.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                }
                else
                {
                    il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                    // Use the helper: iterate and add to new list
                    EmitListFromEnumerableHelper(il);
                }
                break;
            }

            case "dict":
            {
                // dict() → new Dictionary<object, object>()
                // Pop any args if passed (shouldn't be for now)
                for (int i = 0; i < argc; i++)
                    il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Newobj, typeof(Dictionary<object, object>).GetConstructor(Type.EmptyTypes)!);
                break;
            }

            case "set":
            {
                // set(iterable) → new HashSet<object> from iterable
                if (argc == 0)
                {
                    il.Emit(OpCodes.Newobj, typeof(HashSet<object>).GetConstructor(Type.EmptyTypes)!);
                }
                else
                {
                    EmitSetFromEnumerableHelper(il);
                }
                break;
            }

            case "hash":
            {
                // hash(x) → x.GetHashCode()
                var hashArgType = InferStackTopType(callBuiltin, func);
                if (hashArgType.IsValueType)
                    il.Emit(OpCodes.Box, hashArgType);
                il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetHashCode")!);
                break;
            }

            default:
                // Unknown builtin — emit a nop and warning
                _diagnostics.Warning("LEB4001", $"Unknown builtin function '{name}'", SourceSpan.None);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldnull);
                break;
        }
    }

    private void EmitStaticCall(ILGenerator il, string name, int argc)
    {
        if (_methodBuilders.TryGetValue(name, out var mb))
        {
            il.Emit(OpCodes.Call, mb);
        }
        else
        {
            _diagnostics.Warning("LEB4002", $"Unresolved static call to '{name}'", SourceSpan.None);
            // Pop args and push null
            for (int i = 0; i < argc; i++)
                il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
        }
    }

    private void EmitVirtualCall(ILGenerator il, string name, int argc,
        IrInstruction? instr = null, IrFunction? func = null)
    {
        // Handle well-known enumerator methods
        switch (name)
        {
            case "GetEnumerator":
                il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                return;
            case "MoveNext":
                il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                return;
            case "get_Current":
                il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                return;
            case "Add" when argc == 2:
                // Dictionary<object, object>.Add(key, value)
                il.Emit(OpCodes.Callvirt, typeof(Dictionary<object, object>).GetMethod("Add")!);
                return;
            case "Add" when argc == 1:
            {
                // Could be List<object>.Add or HashSet<object>.Add — infer from context
                if (instr is not null && func is not null)
                {
                    var receiverType = InferReceiverType(instr, func);
                    if (receiverType == typeof(HashSet<object>))
                    {
                        // HashSet<object>.Add(object) returns bool
                        il.Emit(OpCodes.Callvirt,
                            typeof(HashSet<object>).GetMethod("Add", [typeof(object)])!);
                        return;
                    }
                }
                // Default: List<object>.Add(object) returns void
                il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add")!);
                return;
            }
        }

        // Try snake_case → PascalCase resolution on common BCL types
        var pascalName = Semantics.DotNetTypeResolver.SnakeToPascal(name);

        // Try resolving on object/string — the most common base types for untyped calls
        var typesToTry = new[] { typeof(string), typeof(object) };
        foreach (var type in typesToTry)
        {
            var method = FindDotNetMethod(type, pascalName, argc, isStatic: false);
            if (method is not null)
            {
                il.Emit(OpCodes.Callvirt, method);
                return;
            }
        }

        // Last resort: pop everything
        for (int i = 0; i < argc + 1; i++)
            il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldnull);
    }

    private void EmitMethodCall(ILGenerator il, string declaringType, string methodName, int argc)
    {
        var key = $"{declaringType}.{methodName}";
        if (_methodBuilders.TryGetValue(key, out var mb))
        {
            // Value types (structs) need special handling: instance methods require
            // a managed pointer (address) as 'this', not the value itself.
            // The stack currently has: [receiver, arg0, arg1, ...argN-1]
            // We save args, store receiver to a temp, ldloca the temp, reload args, then call.
            if (_valueTypeNames.Contains(declaringType) && !mb.IsStatic &&
                _typeBuilders.TryGetValue(declaringType, out var structTb))
            {
                if (argc == 0)
                {
                    // Stack: [receiver]. Store to temp, ldloca, call.
                    var tmp = il.DeclareLocal(structTb);
                    il.Emit(OpCodes.Stloc, tmp);
                    il.Emit(OpCodes.Ldloca, tmp);
                    il.Emit(OpCodes.Call, mb);
                }
                else
                {
                    // Stack: [receiver, arg0, ..., argN-1]. Save args in reverse, store receiver, ldloca, reload args.
                    var argLocals = new LocalBuilder[argc];
                    var paramTypes = mb.GetParameters();
                    for (int i = argc - 1; i >= 0; i--)
                    {
                        argLocals[i] = il.DeclareLocal(paramTypes.Length > i ? paramTypes[i].ParameterType : typeof(object));
                        il.Emit(OpCodes.Stloc, argLocals[i]);
                    }
                    var tmp = il.DeclareLocal(structTb);
                    il.Emit(OpCodes.Stloc, tmp);
                    il.Emit(OpCodes.Ldloca, tmp);
                    for (int i = 0; i < argc; i++)
                        il.Emit(OpCodes.Ldloc, argLocals[i]);
                    il.Emit(OpCodes.Call, mb);
                }
            }
            else if (mb.IsVirtual)
                il.Emit(OpCodes.Callvirt, mb);
            else
                il.Emit(OpCodes.Call, mb);
        }
        else
        {
            EmitVirtualCall(il, methodName, argc);
        }
    }

    private void EmitNewObj(ILGenerator il, string typeName, int argc, IrInstruction instr, IrFunction func)
    {
        if (typeName.StartsWith("System.Collections.Generic.List"))
        {
            var ctor = typeof(List<object>).GetConstructor(Type.EmptyTypes)!;
            il.Emit(OpCodes.Newobj, ctor);
            return;
        }

        if (typeName.StartsWith("System.Collections.Generic.HashSet"))
        {
            var ctor = typeof(HashSet<object>).GetConstructor(Type.EmptyTypes)!;
            il.Emit(OpCodes.Newobj, ctor);
            return;
        }

        if (typeName.StartsWith("System.Collections.Generic.Dictionary"))
        {
            var ctor = typeof(Dictionary<object, object>).GetConstructor(Type.EmptyTypes)!;
            il.Emit(OpCodes.Newobj, ctor);
            return;
        }

        // Look for a user-defined constructor
        if (_constructorBuilders.TryGetValue(typeName, out var cb))
        {
            // Auto-box value type args going to object params (type erasure for generics)
            var ctorParams = cb.GetParameters();
            if (argc > 0 && ctorParams.Any(p => p.ParameterType == typeof(object)))
            {
                // Save all args to typed locals, then reload with boxing where needed
                var argLocals = new LocalBuilder[argc];
                // Pop args in reverse order (stack is LIFO)
                for (int i = argc - 1; i >= 0; i--)
                {
                    // Use object locals — values are either already reference types or need boxing
                    argLocals[i] = il.DeclareLocal(typeof(object));
                    if (ctorParams.Length > i && ctorParams[i].ParameterType == typeof(object))
                    {
                        // Determine the actual value type on the stack
                        var argType = InferNthArgType(instr, func, i, argc);
                        if (argType.IsValueType)
                            il.Emit(OpCodes.Box, argType);
                    }
                    il.Emit(OpCodes.Stloc, argLocals[i]);
                }
                for (int i = 0; i < argc; i++)
                    il.Emit(OpCodes.Ldloc, argLocals[i]);
            }
            il.Emit(OpCodes.Newobj, cb);
            return;
        }

        // Fallback for types without explicit constructors
        if (_typeBuilders.TryGetValue(typeName, out var tb))
        {
            var defaultCtor = tb.DefineDefaultConstructor(MethodAttributes.Public);
            _constructorBuilders[typeName] = defaultCtor;
            il.Emit(OpCodes.Newobj, defaultCtor);
            return;
        }

        il.Emit(OpCodes.Ldnull);
    }

    /// <summary>
    /// Emits a slice operation. Stack has: [object, start(int), stop(int), step(int)].
    /// For lists: creates a new list with the sliced elements.
    /// For strings: creates a substring.
    /// Uses a generated static helper method on the Program class.
    /// </summary>
    private void EmitSliceHelper(ILGenerator il)
    {
        // We emit an inline helper approach:
        // Save step, stop, start, source to locals, then call a runtime helper.
        // The simplest approach: emit a call to a helper we define on the fly.
        if (!_methodBuilders.TryGetValue("<CulebralSlice>", out var sliceHelper))
        {
            // Define the helper on the Program type
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                sliceHelper = programTb.DefineMethod("<CulebralSlice>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(object),
                    [typeof(object), typeof(int), typeof(int), typeof(int)]);
                sliceHelper.DefineParameter(1, ParameterAttributes.None, "source");
                sliceHelper.DefineParameter(2, ParameterAttributes.None, "start");
                sliceHelper.DefineParameter(3, ParameterAttributes.None, "stop");
                sliceHelper.DefineParameter(4, ParameterAttributes.None, "step");
                _methodBuilders["<CulebralSlice>"] = sliceHelper;

                var hil = sliceHelper.GetILGenerator();
                EmitSliceHelperBody(hil);
            }
        }

        if (sliceHelper is not null)
        {
            il.Emit(OpCodes.Call, sliceHelper);
        }
        else
        {
            // Fallback: pop args and push null
            il.Emit(OpCodes.Pop); // step
            il.Emit(OpCodes.Pop); // stop
            il.Emit(OpCodes.Pop); // start
            // source remains — return it as-is
        }
    }

    /// <summary>Emits the body of the CulebralSlice helper method.</summary>
    private static void EmitSliceHelperBody(ILGenerator il)
    {
        // Args: object source (arg0), int start (arg1), int stop (arg2), int step (arg3)
        // If source is string: return substring
        // If source is List<object>: return GetRange
        // stop == -1 means "to end"

        var isStringLabel = il.DefineLabel();
        var isListLabel = il.DefineLabel();
        var returnLabel = il.DefineLabel();

        // Check if source is string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, typeof(string));
        il.Emit(OpCodes.Brtrue, isStringLabel);

        // Check if source is List<object>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, typeof(List<object>));
        il.Emit(OpCodes.Brtrue, isListLabel);

        // Fallback: return source as-is
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);

        // ─── String case ───
        il.MarkLabel(isStringLabel);
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, typeof(string));
            var strLocal = il.DeclareLocal(typeof(string));
            il.Emit(OpCodes.Stloc, strLocal);

            // If stop == -1, set stop = string.Length
            var stopOkLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Bne_Un, stopOkLabel);
            il.Emit(OpCodes.Ldloc, strLocal);
            il.Emit(OpCodes.Callvirt, typeof(string).GetProperty("Length")!.GetGetMethod()!);
            il.Emit(OpCodes.Starg, 2);
            il.MarkLabel(stopOkLabel);

            // return str.Substring(start, stop - start)
            il.Emit(OpCodes.Ldloc, strLocal);
            il.Emit(OpCodes.Ldarg_1); // start
            il.Emit(OpCodes.Ldarg_2); // stop
            il.Emit(OpCodes.Ldarg_1); // start
            il.Emit(OpCodes.Sub);     // stop - start = count
            il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("Substring", [typeof(int), typeof(int)])!);
            il.Emit(OpCodes.Ret);
        }

        // ─── List<object> case ───
        il.MarkLabel(isListLabel);
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, typeof(List<object>));
            var listLocal = il.DeclareLocal(typeof(List<object>));
            il.Emit(OpCodes.Stloc, listLocal);

            // If stop == -1, set stop = list.Count
            var listStopOkLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Bne_Un, listStopOkLabel);
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Callvirt, typeof(List<object>).GetProperty("Count")!.GetGetMethod()!);
            il.Emit(OpCodes.Starg, 2);
            il.MarkLabel(listStopOkLabel);

            // return list.GetRange(start, stop - start)
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Ldarg_1); // start
            il.Emit(OpCodes.Ldarg_2); // stop
            il.Emit(OpCodes.Ldarg_1); // start
            il.Emit(OpCodes.Sub);     // stop - start = count
            il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("GetRange", [typeof(int), typeof(int)])!);
            il.Emit(OpCodes.Ret);
        }
    }

    /// <summary>
    /// Emits truthiness check for an object on the stack.
    /// null -> false, ICollection with Count==0 -> false, else -> true.
    /// </summary>
    private void EmitObjectTruthiness(ILGenerator il)
    {
        var helperName = "<CulebralTruthiness>";
        if (!_methodBuilders.TryGetValue(helperName, out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod(helperName,
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(bool),
                    [typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "value");
                _methodBuilders[helperName] = helper;

                var hil = helper.GetILGenerator();
                var checkCollection = hil.DefineLabel();
                var returnTrue = hil.DefineLabel();

                // if (value == null) return false
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Brtrue, checkCollection);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Ret);

                // if (value is ICollection c) return c.Count > 0
                hil.MarkLabel(checkCollection);
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Isinst, typeof(System.Collections.ICollection));
                hil.Emit(OpCodes.Dup);
                hil.Emit(OpCodes.Brfalse, returnTrue);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.ICollection).GetProperty("Count")!.GetGetMethod()!);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Cgt);
                hil.Emit(OpCodes.Ret);

                // Not a collection and not null -> truthy
                hil.MarkLabel(returnTrue);
                hil.Emit(OpCodes.Pop); // pop the null isinst result
                hil.Emit(OpCodes.Ldc_I4_1);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>
    /// Emits min(iterable) or max(iterable) via a generated helper method.
    /// Iterates the collection using IEnumerable and tracks min/max via Comparer&lt;object&gt;.Default.
    /// </summary>
    private void EmitIterableMinMax(ILGenerator il, bool isMin)
    {
        var helperName = isMin ? "<CulebralIterMin>" : "<CulebralIterMax>";
        if (!_methodBuilders.TryGetValue(helperName, out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod(helperName,
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(object),
                    [typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "source");
                _methodBuilders[helperName] = helper;

                var hil = helper.GetILGenerator();
                var bestLocal = hil.DeclareLocal(typeof(object));
                var currentLocal = hil.DeclareLocal(typeof(object));
                var enumeratorLocal = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                var firstLocal = hil.DeclareLocal(typeof(bool));

                hil.Emit(OpCodes.Ldc_I4_1);
                hil.Emit(OpCodes.Stloc, firstLocal);
                hil.Emit(OpCodes.Ldnull);
                hil.Emit(OpCodes.Stloc, bestLocal);

                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumeratorLocal);

                var loopStart = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();
                var skipUpdate = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                hil.Emit(OpCodes.Ldloc, enumeratorLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);

                hil.Emit(OpCodes.Ldloc, enumeratorLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                hil.Emit(OpCodes.Stloc, currentLocal);

                hil.Emit(OpCodes.Ldloc, firstLocal);
                var notFirst = hil.DefineLabel();
                hil.Emit(OpCodes.Brfalse, notFirst);
                hil.Emit(OpCodes.Ldloc, currentLocal);
                hil.Emit(OpCodes.Stloc, bestLocal);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Stloc, firstLocal);
                hil.Emit(OpCodes.Br, loopStart);

                hil.MarkLabel(notFirst);
                var comparerProp = typeof(Comparer<object>).GetProperty("Default")!.GetGetMethod()!;
                var compareMethod = typeof(Comparer<object>).GetMethod("Compare", [typeof(object), typeof(object)])!;
                hil.Emit(OpCodes.Call, comparerProp);
                hil.Emit(OpCodes.Ldloc, currentLocal);
                hil.Emit(OpCodes.Ldloc, bestLocal);
                hil.Emit(OpCodes.Callvirt, compareMethod);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(isMin ? OpCodes.Bge : OpCodes.Ble, skipUpdate);
                hil.Emit(OpCodes.Ldloc, currentLocal);
                hil.Emit(OpCodes.Stloc, bestLocal);
                hil.MarkLabel(skipUpdate);
                hil.Emit(OpCodes.Br, loopStart);

                hil.MarkLabel(loopEnd);
                hil.Emit(OpCodes.Ldloc, bestLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>
    /// Emits range(start, stop, step) as a List&lt;object&gt; supporting negative step values.
    /// Stack has: [start (int), stop (int), step (int)]
    /// </summary>
    private void EmitRangeWithStep(ILGenerator il)
    {
        var helperName = "<CulebralRangeStep>";
        if (!_methodBuilders.TryGetValue(helperName, out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod(helperName,
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(List<object>),
                    [typeof(int), typeof(int), typeof(int)]);
                helper.DefineParameter(1, ParameterAttributes.None, "start");
                helper.DefineParameter(2, ParameterAttributes.None, "stop");
                helper.DefineParameter(3, ParameterAttributes.None, "step");
                _methodBuilders[helperName] = helper;

                var hil = helper.GetILGenerator();
                var listLocal = hil.DeclareLocal(typeof(List<object>));
                var iLocal = hil.DeclareLocal(typeof(int));

                var stepOk = hil.DefineLabel();
                hil.Emit(OpCodes.Ldarg_2);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Bne_Un, stepOk);
                hil.Emit(OpCodes.Ldstr, "range() arg 3 must not be zero");
                hil.Emit(OpCodes.Newobj, typeof(ArgumentException).GetConstructor([typeof(string)])!);
                hil.Emit(OpCodes.Throw);
                hil.MarkLabel(stepOk);

                hil.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                hil.Emit(OpCodes.Stloc, listLocal);
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Stloc, iLocal);

                var loopStart = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();
                var positiveCheck = hil.DefineLabel();
                var doBody = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                hil.Emit(OpCodes.Ldarg_2);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Bgt, positiveCheck);

                hil.Emit(OpCodes.Ldloc, iLocal);
                hil.Emit(OpCodes.Ldarg_1);
                hil.Emit(OpCodes.Bgt, doBody);
                hil.Emit(OpCodes.Br, loopEnd);

                hil.MarkLabel(positiveCheck);
                hil.Emit(OpCodes.Ldloc, iLocal);
                hil.Emit(OpCodes.Ldarg_1);
                hil.Emit(OpCodes.Blt, doBody);
                hil.Emit(OpCodes.Br, loopEnd);

                hil.MarkLabel(doBody);
                hil.Emit(OpCodes.Ldloc, listLocal);
                hil.Emit(OpCodes.Ldloc, iLocal);
                hil.Emit(OpCodes.Box, typeof(int));
                hil.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add")!);
                hil.Emit(OpCodes.Ldloc, iLocal);
                hil.Emit(OpCodes.Ldarg_2);
                hil.Emit(OpCodes.Add);
                hil.Emit(OpCodes.Stloc, iLocal);
                hil.Emit(OpCodes.Br, loopStart);

                hil.MarkLabel(loopEnd);
                hil.Emit(OpCodes.Ldloc, listLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits sorted(iterable) via a generated helper method.</summary>
    private void EmitSortedHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralSorted>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralSorted>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(List<object>),
                    [typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralSorted>"] = helper;

                var hil = helper.GetILGenerator();
                // var list = new List<object>()
                var listLocal = hil.DeclareLocal(typeof(List<object>));
                hil.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                hil.Emit(OpCodes.Stloc, listLocal);

                // iterate source as IEnumerable, add each to list
                EmitIterateAndCollect(hil, listLocal, argIndex: 0);

                // list.Sort()
                hil.Emit(OpCodes.Ldloc, listLocal);
                hil.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Sort", Type.EmptyTypes)!);

                hil.Emit(OpCodes.Ldloc, listLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits reversed(iterable) via a generated helper method.</summary>
    private void EmitReversedHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralReversed>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralReversed>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(List<object>),
                    [typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralReversed>"] = helper;

                var hil = helper.GetILGenerator();
                var listLocal = hil.DeclareLocal(typeof(List<object>));
                hil.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                hil.Emit(OpCodes.Stloc, listLocal);

                EmitIterateAndCollect(hil, listLocal, argIndex: 0);

                hil.Emit(OpCodes.Ldloc, listLocal);
                hil.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Reverse", Type.EmptyTypes)!);

                hil.Emit(OpCodes.Ldloc, listLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits enumerate(iterable) via a generated helper method.</summary>
    private void EmitEnumerateHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralEnumerate>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralEnumerate>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(List<object>),
                    [typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralEnumerate>"] = helper;

                var hil = helper.GetILGenerator();
                // result = new List<object>()
                var resultLocal = hil.DeclareLocal(typeof(List<object>));
                hil.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                hil.Emit(OpCodes.Stloc, resultLocal);

                // index = 0
                var indexLocal = hil.DeclareLocal(typeof(int));
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Stloc, indexLocal);

                // enumerator = ((IEnumerable)source).GetEnumerator()
                var enumLocal = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumLocal);

                var loopStart = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);

                // Create ValueTuple<object, object>(box(index), current) and box it
                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ldloc, indexLocal);
                hil.Emit(OpCodes.Box, typeof(int));
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                var vtCtor = typeof(ValueTuple<object, object>).GetConstructor([typeof(object), typeof(object)])!;
                hil.Emit(OpCodes.Newobj, vtCtor);
                hil.Emit(OpCodes.Box, typeof(ValueTuple<object, object>));
                hil.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add")!);

                // index++
                hil.Emit(OpCodes.Ldloc, indexLocal);
                hil.Emit(OpCodes.Ldc_I4_1);
                hil.Emit(OpCodes.Add);
                hil.Emit(OpCodes.Stloc, indexLocal);

                hil.Emit(OpCodes.Br, loopStart);
                hil.MarkLabel(loopEnd);

                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits zip(a, b) via a generated helper method.</summary>
    private void EmitZipHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralZip>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralZip>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(List<object>),
                    [typeof(object), typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "a");
                helper.DefineParameter(2, ParameterAttributes.None, "b");
                _methodBuilders["<CulebralZip>"] = helper;

                var hil = helper.GetILGenerator();
                var resultLocal = hil.DeclareLocal(typeof(List<object>));
                hil.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                hil.Emit(OpCodes.Stloc, resultLocal);

                // enumA = ((IEnumerable)a).GetEnumerator()
                var enumA = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumA);

                var enumB = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                hil.Emit(OpCodes.Ldarg_1);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumB);

                var loopStart = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                // if !enumA.MoveNext() → end
                hil.Emit(OpCodes.Ldloc, enumA);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);
                // if !enumB.MoveNext() → end
                hil.Emit(OpCodes.Ldloc, enumB);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);

                // result.Add(box(ValueTuple<object,object>(a.Current, b.Current)))
                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ldloc, enumA);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                hil.Emit(OpCodes.Ldloc, enumB);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                var vtCtor = typeof(ValueTuple<object, object>).GetConstructor([typeof(object), typeof(object)])!;
                hil.Emit(OpCodes.Newobj, vtCtor);
                hil.Emit(OpCodes.Box, typeof(ValueTuple<object, object>));
                hil.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add")!);

                hil.Emit(OpCodes.Br, loopStart);
                hil.MarkLabel(loopEnd);

                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits map(fn, iterable) via a generated helper method.</summary>
    private void EmitMapHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralMap>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralMap>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(List<object>),
                    [typeof(object), typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "fn");
                helper.DefineParameter(2, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralMap>"] = helper;

                var hil = helper.GetILGenerator();
                var resultLocal = hil.DeclareLocal(typeof(List<object>));
                hil.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                hil.Emit(OpCodes.Stloc, resultLocal);

                var delegateLocal = hil.DeclareLocal(typeof(Delegate));
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Castclass, typeof(Delegate));
                hil.Emit(OpCodes.Stloc, delegateLocal);

                var enumLocal = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                hil.Emit(OpCodes.Ldarg_1);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumLocal);

                var currentLocal = hil.DeclareLocal(typeof(object));
                var argsLocal = hil.DeclareLocal(typeof(object[]));

                var loopStart = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);

                // current = enumerator.Current
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                hil.Emit(OpCodes.Stloc, currentLocal);

                // args = new object[] { current }
                hil.Emit(OpCodes.Ldc_I4_1);
                hil.Emit(OpCodes.Newarr, typeof(object));
                hil.Emit(OpCodes.Stloc, argsLocal);
                hil.Emit(OpCodes.Ldloc, argsLocal);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Ldloc, currentLocal);
                hil.Emit(OpCodes.Stelem_Ref);

                // result.Add(delegate.DynamicInvoke(args))
                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ldloc, delegateLocal);
                hil.Emit(OpCodes.Ldloc, argsLocal);
                hil.Emit(OpCodes.Callvirt, typeof(Delegate).GetMethod("DynamicInvoke", [typeof(object[])])!);
                hil.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add")!);

                hil.Emit(OpCodes.Br, loopStart);
                hil.MarkLabel(loopEnd);

                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits filter(fn, iterable) via a generated helper method.</summary>
    private void EmitFilterHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralFilter>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralFilter>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(List<object>),
                    [typeof(object), typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "fn");
                helper.DefineParameter(2, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralFilter>"] = helper;

                var hil = helper.GetILGenerator();
                var resultLocal = hil.DeclareLocal(typeof(List<object>));
                hil.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                hil.Emit(OpCodes.Stloc, resultLocal);

                var enumLocal = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                hil.Emit(OpCodes.Ldarg_1);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumLocal);

                var delegateLocal = hil.DeclareLocal(typeof(Delegate));
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Castclass, typeof(Delegate));
                hil.Emit(OpCodes.Stloc, delegateLocal);

                var currentLocal = hil.DeclareLocal(typeof(object));
                var argsLocal = hil.DeclareLocal(typeof(object[]));

                var loopStart = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();
                var skipAdd = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);

                // current = enumerator.Current
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                hil.Emit(OpCodes.Stloc, currentLocal);

                // args = new object[] { current }
                hil.Emit(OpCodes.Ldc_I4_1);
                hil.Emit(OpCodes.Newarr, typeof(object));
                hil.Emit(OpCodes.Stloc, argsLocal);
                hil.Emit(OpCodes.Ldloc, argsLocal);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Ldloc, currentLocal);
                hil.Emit(OpCodes.Stelem_Ref);

                // result = fn.DynamicInvoke(args)
                hil.Emit(OpCodes.Ldloc, delegateLocal);
                hil.Emit(OpCodes.Ldloc, argsLocal);
                hil.Emit(OpCodes.Callvirt, typeof(Delegate).GetMethod("DynamicInvoke", [typeof(object[])])!);

                // Check truthiness: if result is bool, unbox; otherwise check non-null
                hil.Emit(OpCodes.Unbox_Any, typeof(bool));
                hil.Emit(OpCodes.Brfalse, skipAdd);

                // Add current to result
                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ldloc, currentLocal);
                hil.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add")!);

                hil.MarkLabel(skipAdd);
                hil.Emit(OpCodes.Br, loopStart);
                hil.MarkLabel(loopEnd);

                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits isinstance(x, T) via a generated helper method.</summary>
    private void EmitIsinstanceHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralIsinstance>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralIsinstance>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(bool),
                    [typeof(object), typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "x");
                helper.DefineParameter(2, ParameterAttributes.None, "typeName");
                _methodBuilders["<CulebralIsinstance>"] = helper;

                var hil = helper.GetILGenerator();
                // Get the type name string from arg1 (could be a string like "int", or a Type)
                // We'll compare x.GetType().Name against known type names
                // arg0 = x (object), arg1 = type name (object — typically a string)

                var xTypeLocal = hil.DeclareLocal(typeof(string)); // x's type name
                var typeNameLocal = hil.DeclareLocal(typeof(string)); // target type name

                // Get x's type name
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetType")!);
                hil.Emit(OpCodes.Callvirt, typeof(Type).GetProperty("Name")!.GetGetMethod()!);
                hil.Emit(OpCodes.Stloc, xTypeLocal);

                // Get target type name as string
                hil.Emit(OpCodes.Ldarg_1);
                hil.Emit(OpCodes.Callvirt, typeof(object).GetMethod("ToString")!);
                hil.Emit(OpCodes.Stloc, typeNameLocal);

                // Map Culebral type names to .NET type names and compare
                // We'll do a series of checks: "int" → "Int32", "str" → "String", etc.
                var returnTrue = hil.DefineLabel();
                var returnFalse = hil.DefineLabel();

                // Direct match: x.GetType().Name == typeName.ToString()
                hil.Emit(OpCodes.Ldloc, xTypeLocal);
                hil.Emit(OpCodes.Ldloc, typeNameLocal);
                hil.Emit(OpCodes.Call, typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
                hil.Emit(OpCodes.Brtrue, returnTrue);

                // Map "int" → "Int32"
                EmitTypeNameCheck(hil, xTypeLocal, typeNameLocal, "int", "Int32", returnTrue);
                EmitTypeNameCheck(hil, xTypeLocal, typeNameLocal, "str", "String", returnTrue);
                EmitTypeNameCheck(hil, xTypeLocal, typeNameLocal, "float", "Double", returnTrue);
                EmitTypeNameCheck(hil, xTypeLocal, typeNameLocal, "bool", "Boolean", returnTrue);
                EmitTypeNameCheck(hil, xTypeLocal, typeNameLocal, "list", "List`1", returnTrue);
                EmitTypeNameCheck(hil, xTypeLocal, typeNameLocal, "dict", "Dictionary`2", returnTrue);
                EmitTypeNameCheck(hil, xTypeLocal, typeNameLocal, "set", "HashSet`1", returnTrue);

                hil.MarkLabel(returnFalse);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Ret);

                hil.MarkLabel(returnTrue);
                hil.Emit(OpCodes.Ldc_I4_1);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    private static void EmitTypeNameCheck(ILGenerator il, LocalBuilder xTypeName, LocalBuilder targetName,
        string culebralName, string dotnetName, Label returnTrue)
    {
        var skip = il.DefineLabel();
        // if targetName == culebralName && xTypeName == dotnetName → true
        il.Emit(OpCodes.Ldloc, targetName);
        il.Emit(OpCodes.Ldstr, culebralName);
        il.Emit(OpCodes.Call, typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Brfalse, skip);
        il.Emit(OpCodes.Ldloc, xTypeName);
        il.Emit(OpCodes.Ldstr, dotnetName);
        il.Emit(OpCodes.Call, typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Brtrue, returnTrue);
        il.MarkLabel(skip);
    }

    /// <summary>Emits all(iterable) via a generated helper method.</summary>
    private void EmitAllHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralAll>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralAll>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(bool),
                    [typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralAll>"] = helper;

                var hil = helper.GetILGenerator();
                var enumLocal = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumLocal);

                var loopStart = hil.DefineLabel();
                var returnFalse = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);

                // Check truthiness of current element
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                EmitTruthinessCheck(hil, returnFalse);

                hil.Emit(OpCodes.Br, loopStart);

                hil.MarkLabel(returnFalse);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Ret);

                hil.MarkLabel(loopEnd);
                hil.Emit(OpCodes.Ldc_I4_1);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits any(iterable) via a generated helper method.</summary>
    private void EmitAnyHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralAny>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralAny>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(bool),
                    [typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralAny>"] = helper;

                var hil = helper.GetILGenerator();
                var enumLocal = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumLocal);

                var loopStart = hil.DefineLabel();
                var returnTrue = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();
                var notTruthy = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);

                // Check truthiness of current element — if truthy, return true
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                // For any(): branch to notTruthy if falsy, else return true
                EmitTruthinessCheck(hil, notTruthy);
                hil.Emit(OpCodes.Br, returnTrue);

                hil.MarkLabel(notTruthy);
                hil.Emit(OpCodes.Br, loopStart);

                hil.MarkLabel(returnTrue);
                hil.Emit(OpCodes.Ldc_I4_1);
                hil.Emit(OpCodes.Ret);

                hil.MarkLabel(loopEnd);
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits sum(iterable) via a generated helper method.</summary>
    private void EmitSumHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralSum>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralSum>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(int),
                    [typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralSum>"] = helper;

                var hil = helper.GetILGenerator();
                // accumulator = 0
                var accLocal = hil.DeclareLocal(typeof(int));
                hil.Emit(OpCodes.Ldc_I4_0);
                hil.Emit(OpCodes.Stloc, accLocal);

                var enumLocal = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumLocal);

                var loopStart = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);

                // acc += Convert.ToInt32(current)
                hil.Emit(OpCodes.Ldloc, accLocal);
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                hil.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToInt32", [typeof(object)])!);
                hil.Emit(OpCodes.Add);
                hil.Emit(OpCodes.Stloc, accLocal);

                hil.Emit(OpCodes.Br, loopStart);
                hil.MarkLabel(loopEnd);

                hil.Emit(OpCodes.Ldloc, accLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits list(iterable) — creates a new List&lt;object&gt; from an IEnumerable.</summary>
    private void EmitListFromEnumerableHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralList>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralList>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(List<object>),
                    [typeof(System.Collections.IEnumerable)]);
                helper.DefineParameter(1, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralList>"] = helper;

                var hil = helper.GetILGenerator();
                var resultLocal = hil.DeclareLocal(typeof(List<object>));
                hil.Emit(OpCodes.Newobj, typeof(List<object>).GetConstructor(Type.EmptyTypes)!);
                hil.Emit(OpCodes.Stloc, resultLocal);

                EmitIterateAndCollect(hil, resultLocal, argIndex: 0, sourceIsIEnumerable: true);

                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>Emits set(iterable) — creates a new HashSet&lt;object&gt; from an IEnumerable.</summary>
    private void EmitSetFromEnumerableHelper(ILGenerator il)
    {
        if (!_methodBuilders.TryGetValue("<CulebralSet>", out var helper))
        {
            if (_typeBuilders.TryGetValue("Program", out var programTb))
            {
                helper = programTb.DefineMethod("<CulebralSet>",
                    MethodAttributes.Public | MethodAttributes.Static,
                    typeof(HashSet<object>),
                    [typeof(object)]);
                helper.DefineParameter(1, ParameterAttributes.None, "source");
                _methodBuilders["<CulebralSet>"] = helper;

                var hil = helper.GetILGenerator();
                var resultLocal = hil.DeclareLocal(typeof(HashSet<object>));
                hil.Emit(OpCodes.Newobj, typeof(HashSet<object>).GetConstructor(Type.EmptyTypes)!);
                hil.Emit(OpCodes.Stloc, resultLocal);

                var enumLocal = hil.DeclareLocal(typeof(System.Collections.IEnumerator));
                hil.Emit(OpCodes.Ldarg_0);
                hil.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
                hil.Emit(OpCodes.Stloc, enumLocal);

                var loopStart = hil.DefineLabel();
                var loopEnd = hil.DefineLabel();

                hil.MarkLabel(loopStart);
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
                hil.Emit(OpCodes.Brfalse, loopEnd);

                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ldloc, enumLocal);
                hil.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
                hil.Emit(OpCodes.Callvirt, typeof(HashSet<object>).GetMethod("Add", [typeof(object)])!);
                hil.Emit(OpCodes.Pop); // Add returns bool

                hil.Emit(OpCodes.Br, loopStart);
                hil.MarkLabel(loopEnd);

                hil.Emit(OpCodes.Ldloc, resultLocal);
                hil.Emit(OpCodes.Ret);
            }
        }
        if (helper is not null)
            il.Emit(OpCodes.Call, helper);
    }

    /// <summary>
    /// Helper: Iterate an IEnumerable (from arg at argIndex) and add each element to a List&lt;object&gt;.
    /// Used by sorted, reversed, and list helpers.
    /// </summary>
    private static void EmitIterateAndCollect(ILGenerator il, LocalBuilder listLocal, int argIndex, bool sourceIsIEnumerable = false)
    {
        var enumLocal = il.DeclareLocal(typeof(System.Collections.IEnumerator));
        il.Emit(argIndex == 0 ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1);
        if (!sourceIsIEnumerable)
            il.Emit(OpCodes.Castclass, typeof(System.Collections.IEnumerable));
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, enumLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, loopEnd);

        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, enumLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(List<object>).GetMethod("Add")!);

        il.Emit(OpCodes.Br, loopStart);
        il.MarkLabel(loopEnd);
    }

    /// <summary>
    /// Emits a truthiness check for the value on top of the stack (object).
    /// If falsy (null, false, 0, or ""), branches to falsyLabel.
    /// Consumes the value from the stack.
    /// </summary>
    private static void EmitTruthinessCheck(ILGenerator il, Label falsyLabel)
    {
        var valueLocal = il.DeclareLocal(typeof(object));
        il.Emit(OpCodes.Stloc, valueLocal);

        // Check null
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Brfalse, falsyLabel);

        // Check if bool and false
        var notBool = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, typeof(bool));
        il.Emit(OpCodes.Brfalse, notBool);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Unbox_Any, typeof(bool));
        il.Emit(OpCodes.Brfalse, falsyLabel);
        var done = il.DefineLabel();
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(notBool);

        // Check if int and 0
        var notInt = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, typeof(int));
        il.Emit(OpCodes.Brfalse, notInt);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Unbox_Any, typeof(int));
        il.Emit(OpCodes.Brfalse, falsyLabel);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(notInt);

        // Check if string and empty
        var notString = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, typeof(string));
        il.Emit(OpCodes.Brfalse, notString);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, typeof(string));
        il.Emit(OpCodes.Call, typeof(string).GetMethod("IsNullOrEmpty", [typeof(string)])!);
        il.Emit(OpCodes.Brtrue, falsyLabel);
        il.Emit(OpCodes.Br, done);
        il.MarkLabel(notString);

        // Otherwise it's truthy (non-null, non-bool, non-int, non-string object)
        il.MarkLabel(done);
    }

    private void EmitToString(ILGenerator il, CulebralType sourceType)
    {
        // If already a string, nothing to do
        if (sourceType == PrimitiveType.Str)
            return;

        // For value types, box then call ToString
        var clrType = ResolveClrType(sourceType);
        if (clrType.IsValueType)
        {
            il.Emit(OpCodes.Box, clrType);
        }

        // Call object.ToString()
        var toString = typeof(object).GetMethod("ToString", Type.EmptyTypes)!;
        il.Emit(OpCodes.Callvirt, toString);
    }

    private static void EmitStringConcat(ILGenerator il, int partCount)
    {
        if (partCount <= 1) return;

        // Use String.Concat(string, string) iteratively
        var concatTwo = typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!;
        for (int i = 1; i < partCount; i++)
        {
            il.Emit(OpCodes.Call, concatTwo);
        }
    }

    private void EmitCast(ILGenerator il, string typeName)
    {
        var type = ResolveClrTypeByName(typeName);
        if (type is not null)
            il.Emit(OpCodes.Castclass, type);
    }

    private void EmitIsInst(ILGenerator il, string typeName)
    {
        var type = ResolveClrTypeByName(typeName);
        if (type is not null)
        {
            il.Emit(OpCodes.Isinst, type);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Cgt_Un); // Convert to bool
        }
    }

    // ─── Stack Type Inference ───

    /// <summary>
    /// Determines the CLR type of the value on top of the stack at the given instruction.
    /// Walks backward through the current basic block's instructions.
    /// </summary>
    /// <summary>
    /// Infers the receiver type for a virtual call by scanning backward in the IR
    /// to find the IrNewObj that created the collection being called on.
    /// </summary>
    private Type InferReceiverType(IrInstruction target, IrFunction func)
    {
        foreach (var block in func.Body)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                if (!ReferenceEquals(block.Instructions[i], target)) continue;

                // Scan backward to find the nearest IrNewObj for a collection type
                for (int j = i - 1; j >= 0; j--)
                {
                    if (block.Instructions[j] is IrNewObj { TypeName: var tn })
                    {
                        if (tn.StartsWith("System.Collections.Generic.HashSet"))
                            return typeof(HashSet<object>);
                        if (tn.StartsWith("System.Collections.Generic.Dictionary"))
                            return typeof(Dictionary<object, object>);
                        if (tn.StartsWith("System.Collections.Generic.List"))
                            return typeof(List<object>);
                    }
                }
            }
        }
        return typeof(List<object>); // Safe default — List<object>.Add is the original behavior
    }

    private Type InferStackTopType(IrInstruction target, IrFunction func)
    {
        // Find the basic block and instruction index
        foreach (var block in func.Body)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                if (ReferenceEquals(block.Instructions[i], target) && i > 0)
                {
                    return InferInstructionResultType(block.Instructions[i - 1], func);
                }
            }
        }
        return typeof(object);
    }

    private Type InferInstructionResultType(IrInstruction instr, IrFunction func)
    {
        return instr switch
        {
            IrLoadInt => typeof(int),
            IrLoadFloat => typeof(double),
            IrLoadString => typeof(string),
            IrLoadBool => typeof(bool),
            IrLoadNull => typeof(object),
            IrLoadLocal { Index: var idx } when idx < func.Locals.Count
                => ResolveClrType(func.Locals[idx].Type),
            IrLoadArg { Index: var idx } when idx < func.Parameters.Count
                => ResolveClrType(func.Parameters[idx].Type),
            IrBinaryOp { Op: IrBinaryOpKind.Equal or IrBinaryOpKind.NotEqual or
                IrBinaryOpKind.LessThan or IrBinaryOpKind.GreaterThan or
                IrBinaryOpKind.LessEqual or IrBinaryOpKind.GreaterEqual }
                => typeof(bool),
            IrBinaryOp { Op: IrBinaryOpKind.Add, OperandType: PrimitiveType { Name: "str" } }
                => typeof(string),
            IrBinaryOp { Op: IrBinaryOpKind.LogicalAnd or IrBinaryOpKind.LogicalOr }
                => typeof(bool),
            IrBinaryOp { OperandType: PrimitiveType { Name: "float" } }
                => typeof(double),
            IrBinaryOp { Op: IrBinaryOpKind.Add or IrBinaryOpKind.Sub or
                IrBinaryOpKind.Mul or IrBinaryOpKind.Mod or IrBinaryOpKind.IntDiv }
                => typeof(int),
            IrBinaryOp { Op: IrBinaryOpKind.Div or IrBinaryOpKind.Pow } => typeof(double),
            IrCall { FunctionName: var name } when _methodBuilders.TryGetValue(name, out var mb)
                => mb.ReturnType,
            IrCallMethod { DeclaringType: var dt, MethodName: var mn }
                when _methodBuilders.TryGetValue($"{dt}.{mn}", out var cmb)
                => cmb.ReturnType,
            IrNewObj { TypeName: var tn } when tn.StartsWith("System.Collections.Generic.HashSet")
                => typeof(HashSet<object>),
            IrNewObj { TypeName: var tn } when tn.StartsWith("System.Collections.Generic.Dictionary")
                => typeof(Dictionary<object, object>),
            IrNewObj { TypeName: var tn } when tn.StartsWith("System.Collections.Generic.List")
                => typeof(List<object>),
            IrNewObj { TypeName: var tn } when _typeBuilders.TryGetValue(tn, out var ntb)
                => ntb,
            IrLoadField { DeclaringType: var fdt, FieldName: var fn }
                when _fieldBuilders.TryGetValue($"{fdt}.{fn}", out var lfb)
                => lfb.FieldType,
            IrCallBuiltin { Name: "round", ArgCount: 2 } => typeof(double),
            IrCallBuiltin { Name: "min" or "max", ArgCount: 1 } => typeof(object),
            IrCallBuiltin { Name: "len" or "int" or "ord" or "round" or "abs" or "min" or "max" or "sum" or "hash" } => typeof(int),
            IrCallBuiltin { Name: "float" } => typeof(double),
            IrCallBuiltin { Name: "str" or "chr" or "type" or "input" } => typeof(string),
            IrCallBuiltin { Name: "bool" or "all" or "any" or "isinstance" } => typeof(bool),
            IrCallBuiltin { Name: "sorted" or "reversed" or "enumerate" or "zip" or "map" or "filter" or "list" } => typeof(List<object>),
            IrCallBuiltin { Name: "set" } => typeof(HashSet<object>),
            IrCallBuiltin { Name: "dict" } => typeof(Dictionary<object, object>),
            IrUnaryOp { Op: IrUnaryOpKind.LogicalNot } => typeof(bool),
            IrUnaryOp { Op: IrUnaryOpKind.Negate } => typeof(int),
            IrToString => typeof(string),
            IrStringConcat => typeof(string),
            // .NET interop
            IrCallVirtual { MethodName: var vn, ArgCount: var va } =>
                ResolveVirtualCallReturnType(vn, va),
            IrCallDotNetStatic { DeclaringType: var t, MethodName: var n, ArgCount: var a }
                => FindDotNetMethod(t, n, a, true)?.ReturnType ?? typeof(object),
            IrCallDotNetInstance { DeclaringType: var t, MethodName: var n, ArgCount: var a }
                => FindDotNetMethod(t, n, a, false)?.ReturnType ?? typeof(object),
            IrLoadDotNetProperty { DeclaringType: var t, PropertyName: var n, IsStatic: var s }
                => t.GetProperty(n, BindingFlags.Public | (s ? BindingFlags.Static : BindingFlags.Instance))?.PropertyType ?? typeof(object),
            IrNewDotNetObj { Type: var t } => t,
            IrCallDotNetGenericStatic { DeclaringType: var gt, MethodName: var gn, ArgCount: var ga, TypeArguments: var gta }
                => ResolveGenericMethodReturnType(gt, gn, ga, gta, true),
            IrCallDotNetGenericInstance { DeclaringType: var gt, MethodName: var gn, ArgCount: var ga, TypeArguments: var gta }
                => ResolveGenericMethodReturnType(gt, gn, ga, gta, false),
            // Extension method results are auto-boxed for value types, so stack type is always object
            IrCallExtensionMethod => typeof(object),
            IrCreateDelegate => typeof(Delegate),
            IrInvokeDelegate => typeof(object),
            IrSlice => typeof(object),
            IrNewArrayFromStack => typeof(object[]),
            IrListConcat => typeof(List<object>),
            IrListRepeat => typeof(List<object>),
            IrStringRepeat => typeof(string),
            _ => typeof(object),
        };
    }

    private static Type ResolveGenericMethodReturnType(Type type, string name, int argCount, Type[] typeArgs, bool isStatic)
    {
        var method = FindDotNetGenericMethod(type, name, argCount, typeArgs.Length, isStatic);
        if (method is null) return typeof(object);
        try
        {
            var closed = method.MakeGenericMethod(typeArgs);
            return closed.ReturnType;
        }
        catch
        {
            return typeof(object);
        }
    }

    private static Type ResolveVirtualCallReturnType(string name, int argc)
    {
        var pascal = Semantics.DotNetTypeResolver.SnakeToPascal(name);
        foreach (var type in new[] { typeof(string), typeof(object) })
        {
            var m = FindDotNetMethod(type, pascal, argc, false);
            if (m is not null) return m.ReturnType;
        }
        return typeof(object);
    }

    private static MethodInfo? FindDotNetMethod(Type type, string name, int argCount, bool isStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var candidates = type.GetMethods(flags)
            .Where(m => m.Name == name && m.GetParameters().Length == argCount && !m.IsGenericMethod)
            .ToArray();

        if (candidates.Length == 0)
        {
            // Case-insensitive fallback
            candidates = type.GetMethods(flags)
                .Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                             && m.GetParameters().Length == argCount && !m.IsGenericMethod)
                .ToArray();
        }

        if (candidates.Length <= 1)
            return candidates.FirstOrDefault();

        // Overload resolution: prefer common Culebral types
        return candidates
            .OrderByDescending(m => m.GetParameters().Sum(p => p.ParameterType switch
            {
                var t when t == typeof(string) => 10,
                var t when t == typeof(int) => 9,
                var t when t == typeof(double) => 8,
                var t when t == typeof(bool) => 7,
                var t when t == typeof(long) => 6,
                var t when t == typeof(object) => 5,
                _ => 0,
            }))
            .First();
    }

    /// <summary>
    /// Find an open generic method definition on a .NET type.
    /// Returns the MethodInfo before MakeGenericMethod — caller must close it.
    /// </summary>
    private static MethodInfo? FindDotNetGenericMethod(Type type, string name, int argCount, int typeArgCount, bool isStatic)
    {
        var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                    (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var candidates = type.GetMethods(flags)
            .Where(m => m.Name == name
                        && m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == typeArgCount
                        && m.GetParameters().Length == argCount)
            .ToArray();

        if (candidates.Length == 0)
        {
            // Case-insensitive fallback
            candidates = type.GetMethods(flags)
                .Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                             && m.IsGenericMethodDefinition
                             && m.GetGenericArguments().Length == typeArgCount
                             && m.GetParameters().Length == argCount)
                .ToArray();
        }

        return candidates.FirstOrDefault();
    }

    /// <summary>
    /// Find an extension method on a source type. Extension methods are static with [Extension] attribute.
    /// argCount is the explicit arg count (not counting the receiver which is already on stack as first param).
    /// </summary>
    private static MethodInfo? FindExtensionMethod(Type extensionSourceType, string name, int argCount, Type[]? typeArgs)
    {
        var flags = BindingFlags.Public | BindingFlags.Static;
        var isGeneric = typeArgs is not null;

        var candidates = extensionSourceType.GetMethods(flags)
            .Where(m => m.Name == name
                        && m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)
                        && m.GetParameters().Length == argCount + 1  // +1 for receiver
                        && (isGeneric ? m.IsGenericMethodDefinition : !m.IsGenericMethodDefinition))
            .ToArray();

        if (candidates.Length == 0)
        {
            // Case-insensitive fallback
            candidates = extensionSourceType.GetMethods(flags)
                .Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                             && m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)
                             && m.GetParameters().Length == argCount + 1
                             && (isGeneric ? m.IsGenericMethodDefinition : !m.IsGenericMethodDefinition))
                .ToArray();
        }

        if (candidates.Length == 0 && !isGeneric)
        {
            // Also try generic extension methods even when typeArgs is null —
            // some extension methods are always generic (like Count<T>)
            candidates = extensionSourceType.GetMethods(flags)
                .Where(m => m.Name == name
                            && m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)
                            && m.GetParameters().Length == argCount + 1
                            && m.IsGenericMethodDefinition)
                .ToArray();
        }

        return candidates.FirstOrDefault();
    }

    /// <summary>Infer the type of the Nth argument (0-based) before a call instruction.</summary>
    private Type InferNthArgType(IrInstruction callInstr, IrFunction func, int argIndex, int totalArgs)
    {
        // Walk backwards from the call instruction to find the instruction that produces the Nth arg
        foreach (var block in func.Body)
        {
            for (int i = 0; i < block.Instructions.Count; i++)
            {
                if (!ReferenceEquals(block.Instructions[i], callInstr)) continue;
                // The args are the `totalArgs` instructions before this one
                var targetIdx = i - totalArgs + argIndex;
                if (targetIdx >= 0 && targetIdx < i)
                    return InferInstructionResultType(block.Instructions[targetIdx], func);
            }
        }
        return typeof(object);
    }

    // ─── Assembly Save ───

    private void SaveAssembly(IrModule module)
    {
        var outputDir = Path.GetDirectoryName(_outputPath) ?? ".";
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        var metadataBuilder = _assemblyBuilder.GenerateMetadata(
            out var ilStream,
            out var fieldData);

        var peHeaderBuilder = _entryPointMethod is not null
            ? PEHeaderBuilder.CreateExecutableHeader()
            : PEHeaderBuilder.CreateLibraryHeader();

        var entryPointHandle = _entryPointMethod is not null
            ? MetadataTokens.MethodDefinitionHandle(
                _entryPointMethod.MetadataToken)
            : default;

        var peBuilder = new ManagedPEBuilder(
            header: peHeaderBuilder,
            metadataRootBuilder: new MetadataRootBuilder(metadataBuilder),
            ilStream: ilStream,
            mappedFieldData: fieldData,
            entryPoint: entryPointHandle);

        var blobBuilder = new BlobBuilder();
        peBuilder.Serialize(blobBuilder);

        using var fs = new FileStream(_outputPath, FileMode.Create, FileAccess.Write);
        blobBuilder.WriteContentTo(fs);
    }

    private void GenerateRuntimeConfig(string assemblyName)
    {
        var configPath = Path.ChangeExtension(_outputPath, ".runtimeconfig.json");
        var config = $$"""
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "framework": {
              "name": "Microsoft.NETCore.App",
              "version": "10.0.0"
            }
          }
        }
        """;
        File.WriteAllText(configPath, config);
    }

    // ─── Type Resolution ───

    private Type ResolveClrType(CulebralType type)
    {
        if (type.ClrType is not null)
            return type.ClrType;

        return type switch
        {
            PrimitiveType p => p.ClrBackingType,
            NullableCulebralType n => ResolveClrType(n.Inner),
            GenericInstanceType g => ResolveGenericClrType(g),
            ClassType c => _typeBuilders.TryGetValue(c.Name, out var tb) ? tb : typeof(object),
            StructType s => _typeBuilders.TryGetValue(s.Name, out var tb) ? tb : typeof(object),
            RecordType r => _typeBuilders.TryGetValue(r.Name, out var tb) ? tb : typeof(object),
            DotNetType dt => dt.ClrBackingType,
            TypeParameterType => typeof(object),
            FunctionType => typeof(Delegate),
            _ => typeof(object),
        };
    }

    private Type ResolveGenericClrType(GenericInstanceType generic)
    {
        return generic.Name switch
        {
            "list" => typeof(List<object>), // Simplified
            "dict" => typeof(Dictionary<object, object>),
            "set" => typeof(HashSet<object>),
            "array" => typeof(object[]),
            _ => typeof(object),
        };
    }

    private Type? ResolveClrTypeByName(string name)
    {
        if (_typeBuilders.TryGetValue(name, out var tb))
            return tb;

        return name switch
        {
            "int" => typeof(int),
            "long" => typeof(long),
            "float" => typeof(double),
            "bool" => typeof(bool),
            "str" or "string" => typeof(string),
            "object" => typeof(object),
            _ => Type.GetType(name),
        };
    }

    private static TypeAttributes GetTypeAttributes(IrTypeDef typeDef)
    {
        var attrs = TypeAttributes.Public;
        attrs |= typeDef.Kind switch
        {
            IrTypeKind.Class => TypeAttributes.Class,
            IrTypeKind.Struct => TypeAttributes.SequentialLayout | TypeAttributes.Sealed,
            IrTypeKind.Record => TypeAttributes.Class | TypeAttributes.Sealed,
            IrTypeKind.Interface => TypeAttributes.Interface | TypeAttributes.Abstract,
            IrTypeKind.SealedClass => TypeAttributes.Class | TypeAttributes.Sealed,
            IrTypeKind.AbstractClass => TypeAttributes.Class | TypeAttributes.Abstract,
            _ => TypeAttributes.Class,
        };
        return attrs;
    }
}
