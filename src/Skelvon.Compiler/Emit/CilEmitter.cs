using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Skelvon.Compiler.Diagnostics;
using Skelvon.Compiler.IR;
using Skelvon.Compiler.Semantics;

namespace Skelvon.Compiler.Emit;

/// <summary>
/// Emits .NET CIL bytecode from SkelvonIR using PersistedAssemblyBuilder.
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
            _diagnostics.Error("SKV4000", $"CIL emission failed: {ex.Message}", SourceSpan.None);
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
                    // Structs are emitted as sealed classes for now; true value-type semantics in Phase 4
                    _ => typeof(object),
                };
                tb = _moduleBuilder.DefineType(typeDef.Name, attrs, parent);
            }

            _typeBuilders[typeDef.Name] = tb;
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

        // Call base constructor
        if (true) // Structs now emitted as sealed classes, so they chain normally
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

        // Call base constructor
        il.Emit(OpCodes.Ldarg_0);
        var baseCtor = typeof(object).GetConstructor(Type.EmptyTypes)!;
        il.Emit(OpCodes.Call, baseCtor);

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

    private void EmitMethod(TypeBuilder tb, IrFunction method, IrTypeDef typeDef)
    {
        var returnClrType = ResolveClrType(method.ReturnType);
        var paramClrTypes = method.Parameters.Select(p => ResolveClrType(p.Type)).ToArray();

        var methodAttrs = MethodAttributes.Public | MethodAttributes.HideBySig;
        if (method.IsStatic)
            methodAttrs |= MethodAttributes.Static;
        if (typeDef.Kind == IrTypeKind.Interface)
            methodAttrs |= MethodAttributes.Abstract | MethodAttributes.Virtual | MethodAttributes.NewSlot;
        else if (!method.IsStatic && typeDef.Interfaces.Count > 0)
            methodAttrs |= MethodAttributes.Virtual; // For interface implementation

        var mb = tb.DefineMethod(method.Name, methodAttrs, returnClrType, paramClrTypes);

        // Name parameters
        for (int i = 0; i < method.Parameters.Count; i++)
            mb.DefineParameter(i + 1, ParameterAttributes.None, method.Parameters[i].Name);

        _methodBuilders[$"{typeDef.Name}.{method.Name}"] = mb;

        // Don't emit body for abstract/interface methods
        if (typeDef.Kind == IrTypeKind.Interface)
            return;

        var il = mb.GetILGenerator();
        EmitFunctionBody(il, method);
    }

    private void EmitProperty(TypeBuilder tb, IrProperty prop, IrTypeDef typeDef)
    {
        var clrType = ResolveClrType(prop.Type);
        var pb = tb.DefineProperty(prop.Name, PropertyAttributes.None, clrType, Type.EmptyTypes);

        if (prop.Getter is not null)
        {
            var getterMb = tb.DefineMethod($"get_{prop.Name}",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                clrType, Type.EmptyTypes);
            _methodBuilders[$"{typeDef.Name}.get_{prop.Name}"] = getterMb;
            var il = getterMb.GetILGenerator();
            EmitFunctionBody(il, prop.Getter);
            pb.SetGetMethod(getterMb);
        }

        if (prop.Setter is not null)
        {
            var setterMb = tb.DefineMethod($"set_{prop.Name}",
                MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
                typeof(void), [clrType]);
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

        // Pass 1: Define all method signatures (enables forward references / mutual recursion)
        var functionsToEmit = new List<(IrFunction Func, MethodBuilder Builder)>();
        foreach (var func in module.Functions)
        {
            if (func.DeclaringType is not null)
                continue;

            var returnClrType = ResolveClrType(func.ReturnType);
            var paramClrTypes = func.Parameters.Select(p => ResolveClrType(p.Type)).ToArray();

            var methodAttrs = MethodAttributes.Public | MethodAttributes.Static;
            var mb = programType.DefineMethod(
                func.IsEntryPoint ? "Main" : func.Name,
                methodAttrs, returnClrType, paramClrTypes);

            for (int i = 0; i < func.Parameters.Count; i++)
                mb.DefineParameter(i + 1, ParameterAttributes.None, func.Parameters[i].Name);

            _methodBuilders[func.Name] = mb;
            functionsToEmit.Add((func, mb));

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

    private void EmitFunctionBody(ILGenerator il, IrFunction func)
    {
        // Declare locals
        var locals = new LocalBuilder[func.Locals.Count];
        for (int i = 0; i < func.Locals.Count; i++)
        {
            var clrType = ResolveClrType(func.Locals[i].Type);
            locals[i] = il.DeclareLocal(clrType);
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
                EmitInstruction(il, instr, locals, labels, func);
            }
        }
    }

    private void EmitInstruction(ILGenerator il, IrInstruction instr,
        LocalBuilder[] locals, Dictionary<string, Label> labels, IrFunction func)
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
                il.Emit(OpCodes.Ret);
                break;

            case IrReturn { HasValue: true }:
                il.Emit(OpCodes.Ret);
                break;

            case IrCallBuiltin callBuiltin:
                EmitBuiltinCall(il, callBuiltin, func, locals);
                break;

            case IrCall { FunctionName: var name, ArgCount: var argc, IsStatic: true }:
                EmitStaticCall(il, name, argc);
                break;

            case IrCallVirtual { MethodName: var name, ArgCount: var argc }:
                EmitVirtualCall(il, name, argc);
                break;

            case IrCallMethod { DeclaringType: var dt, MethodName: var mn, ArgCount: var ac }:
                EmitMethodCall(il, dt, mn, ac);
                break;

            case IrNewObj { TypeName: var typeName, ArgCount: var argc }:
                EmitNewObj(il, typeName, argc);
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
                    il.Emit(OpCodes.Stfld, storeFb);
                break;
            }

            case IrCastClass { TypeName: var name }:
                EmitCast(il, name);
                break;

            case IrIsInst { TypeName: var name }:
                EmitIsInst(il, name);
                break;

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

    private static void EmitBinaryOp(ILGenerator il, IrBinaryOpKind op, SkelvonType? operandType = null)
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
            case IrBinaryOpKind.Div: il.Emit(OpCodes.Div); break;
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
                // Math.Pow(a, b)
                il.Emit(OpCodes.Conv_R8);
                var mathPow = typeof(Math).GetMethod("Pow", [typeof(double), typeof(double)])!;
                il.Emit(OpCodes.Call, mathPow);
                break;
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
                // For strings, call .Length
                var strLength = typeof(string).GetProperty("Length")!.GetGetMethod()!;
                il.Emit(OpCodes.Callvirt, strLength);
                break;

            case "range":
                // Use Enumerable.Range — simplified to single-arg form
                var enumRange = typeof(Enumerable).GetMethod("Range", [typeof(int), typeof(int)])!;
                // Push 0 as start, arg is count
                // For single arg range(n), emit Range(0, n)
                // The arg is already on stack as 'count'
                // We need to insert 0 before it — use a local
                var tmpLocal = il.DeclareLocal(typeof(int));
                il.Emit(OpCodes.Stloc, tmpLocal);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldloc, tmpLocal);
                il.Emit(OpCodes.Call, enumRange);
                break;

            case "int":
                var convertToInt = typeof(Convert).GetMethod("ToInt32", [typeof(object)])!;
                il.Emit(OpCodes.Call, convertToInt);
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
                // Math.Abs — need overload resolution, use double version
                var mathAbs = typeof(Math).GetMethod("Abs", [typeof(double)])!;
                il.Emit(OpCodes.Call, mathAbs);
                break;

            default:
                // Unknown builtin — emit a nop and warning
                _diagnostics.Warning("SKV4001", $"Unknown builtin function '{name}'", SourceSpan.None);
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
            _diagnostics.Warning("SKV4002", $"Unresolved static call to '{name}'", SourceSpan.None);
            // Pop args and push null
            for (int i = 0; i < argc; i++)
                il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldnull);
        }
    }

    private static void EmitVirtualCall(ILGenerator il, string name, int argc)
    {
        // For now, handle common method names
        switch (name)
        {
            case "GetEnumerator":
                var getEnumerator = typeof(System.Collections.IEnumerable).GetMethod("GetEnumerator")!;
                il.Emit(OpCodes.Callvirt, getEnumerator);
                break;
            case "MoveNext":
                var moveNext = typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!;
                il.Emit(OpCodes.Callvirt, moveNext);
                break;
            case "get_Current":
                var getCurrent = typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!;
                il.Emit(OpCodes.Callvirt, getCurrent);
                break;
            case "Add":
                // Generic List.Add — use object version
                var addMethod = typeof(List<object>).GetMethod("Add")!;
                il.Emit(OpCodes.Callvirt, addMethod);
                break;
            case "ToString":
                var toString = typeof(object).GetMethod("ToString", Type.EmptyTypes)!;
                il.Emit(OpCodes.Callvirt, toString);
                break;
            default:
                // Unknown virtual call — pop args + this, push null
                for (int i = 0; i < argc + 1; i++)
                    il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldnull);
                break;
        }
    }

    private void EmitMethodCall(ILGenerator il, string declaringType, string methodName, int argc)
    {
        var key = $"{declaringType}.{methodName}";
        if (_methodBuilders.TryGetValue(key, out var mb))
        {
            // Use Call for non-virtual methods, Callvirt for virtual
            if (mb.IsVirtual)
                il.Emit(OpCodes.Callvirt, mb);
            else
                il.Emit(OpCodes.Call, mb);
        }
        else
        {
            EmitVirtualCall(il, methodName, argc);
        }
    }

    private void EmitNewObj(ILGenerator il, string typeName, int argc)
    {
        if (typeName.StartsWith("System.Collections.Generic.List"))
        {
            var ctor = typeof(List<object>).GetConstructor(Type.EmptyTypes)!;
            il.Emit(OpCodes.Newobj, ctor);
            return;
        }

        // Look for a user-defined constructor
        if (_constructorBuilders.TryGetValue(typeName, out var cb))
        {
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

    private void EmitToString(ILGenerator il, SkelvonType sourceType)
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
                IrBinaryOpKind.Mul or IrBinaryOpKind.Mod }
                => typeof(int),
            IrBinaryOp { Op: IrBinaryOpKind.Div } => typeof(double),
            IrCall { FunctionName: var name } when _methodBuilders.TryGetValue(name, out var mb)
                => mb.ReturnType,
            IrCallMethod { DeclaringType: var dt, MethodName: var mn }
                when _methodBuilders.TryGetValue($"{dt}.{mn}", out var cmb)
                => cmb.ReturnType,
            IrNewObj { TypeName: var tn } when _typeBuilders.TryGetValue(tn, out var ntb)
                => ntb,
            IrLoadField { DeclaringType: var fdt, FieldName: var fn }
                when _fieldBuilders.TryGetValue($"{fdt}.{fn}", out var lfb)
                => lfb.FieldType,
            IrCallBuiltin { Name: "len" or "int" } => typeof(int),
            IrCallBuiltin { Name: "float" } => typeof(double),
            IrCallBuiltin { Name: "str" } => typeof(string),
            IrCallBuiltin { Name: "bool" } => typeof(bool),
            IrUnaryOp { Op: IrUnaryOpKind.LogicalNot } => typeof(bool),
            IrUnaryOp { Op: IrUnaryOpKind.Negate } => typeof(int),
            IrToString => typeof(string),
            IrStringConcat => typeof(string),
            _ => typeof(object),
        };
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

    private Type ResolveClrType(SkelvonType type)
    {
        if (type.ClrType is not null)
            return type.ClrType;

        return type switch
        {
            PrimitiveType p => p.ClrBackingType,
            NullableSkelvonType n => ResolveClrType(n.Inner),
            GenericInstanceType g => ResolveGenericClrType(g),
            ClassType c => _typeBuilders.TryGetValue(c.Name, out var tb) ? tb : typeof(object),
            StructType s => _typeBuilders.TryGetValue(s.Name, out var tb) ? tb : typeof(object),
            RecordType r => _typeBuilders.TryGetValue(r.Name, out var tb) ? tb : typeof(object),
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
            IrTypeKind.Struct => TypeAttributes.Class | TypeAttributes.Sealed,
            IrTypeKind.Record => TypeAttributes.Class | TypeAttributes.Sealed,
            IrTypeKind.Interface => TypeAttributes.Interface | TypeAttributes.Abstract,
            IrTypeKind.SealedClass => TypeAttributes.Class | TypeAttributes.Sealed,
            IrTypeKind.AbstractClass => TypeAttributes.Class | TypeAttributes.Abstract,
            _ => TypeAttributes.Class,
        };
        return attrs;
    }
}
