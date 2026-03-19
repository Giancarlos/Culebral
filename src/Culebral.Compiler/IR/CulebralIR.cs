using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Semantics;

namespace Culebral.Compiler.IR;

// ─── Module (Top-Level Container) ───

/// <summary>
/// A compiled module — the IR for one .leb file.
/// Contains type definitions, function definitions, and module-level code.
/// </summary>
public sealed class IrModule
{
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public List<IrTypeDef> Types { get; } = [];
    public List<IrFunction> Functions { get; } = [];
    public IrFunction? EntryPoint { get; set; }
}

// ─── Type Definitions ───

public sealed class IrTypeDef
{
    public required string Name { get; init; }
    public required IrTypeKind Kind { get; init; }
    public List<IrField> Fields { get; } = [];
    public List<IrFunction> Methods { get; } = [];
    public List<IrProperty> Properties { get; } = [];
    public List<string> Interfaces { get; } = [];
    public string? BaseType { get; init; }
    public IrFunction? Constructor { get; set; }

    /// <summary>Generic type parameters with optional constraints (e.g., T: Printable).</summary>
    public List<IrTypeParameter> TypeParameters { get; } = [];
}

/// <summary>A generic type parameter with an optional constraint type name.</summary>
public sealed class IrTypeParameter
{
    public required string Name { get; init; }
    /// <summary>The constraint type name (interface or base class), or null if unconstrained.</summary>
    public string? ConstraintTypeName { get; init; }
}

public enum IrTypeKind
{
    Class,
    Struct,
    Record,
    Interface,
    SealedClass,     // For enum variant classes
    AbstractClass,   // For enum base class
}

public sealed class IrField
{
    public required string Name { get; init; }
    public required CulebralType Type { get; init; }
    public IrInstruction? DefaultValue { get; init; }
    public bool IsStatic { get; init; }
}

public sealed class IrProperty
{
    public required string Name { get; init; }
    public required CulebralType Type { get; init; }
    public IrFunction? Getter { get; init; }
    public IrFunction? Setter { get; init; }
}

// ─── Functions ───

public sealed class IrFunction
{
    public required string Name { get; init; }
    public required CulebralType ReturnType { get; init; }
    public required List<IrParameter> Parameters { get; init; }
    public required List<IrBasicBlock> Body { get; init; }
    public bool IsStatic { get; init; } = true;
    public bool IsAsync { get; init; }
    public bool IsGenerator { get; set; }
    public bool IsEntryPoint { get; init; }
    public string? DeclaringType { get; init; }

    /// <summary>Decorator names from the source, for attribute emission.</summary>
    public List<string>? Decorators { get; init; }

    /// <summary>Local variable declarations, in order of first use.</summary>
    public List<IrLocal> Locals { get; } = [];
}

public sealed class IrParameter
{
    public required string Name { get; init; }
    public required CulebralType Type { get; init; }
    public int Index { get; init; }
    public bool IsVarArgs { get; init; }
}

public sealed class IrLocal
{
    public required string Name { get; init; }
    public required CulebralType Type { get; init; }
    public int Index { get; set; }
}

// ─── Basic Blocks ───

public sealed class IrBasicBlock
{
    public required string Label { get; init; }
    public List<IrInstruction> Instructions { get; } = [];

    public void Emit(IrInstruction instruction) => Instructions.Add(instruction);
}

// ─── Instructions ───

public abstract record IrInstruction(SourceSpan Span);

// Constants
public sealed record IrLoadInt(long Value, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadFloat(double Value, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadString(string Value, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadBool(bool Value, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadNull(SourceSpan Span) : IrInstruction(Span);

// Variables
public sealed record IrLoadLocal(int Index, SourceSpan Span) : IrInstruction(Span);
public sealed record IrStoreLocal(int Index, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadArg(int Index, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadThis(SourceSpan Span) : IrInstruction(Span);

// Field access — DeclaringType is the fully-qualified type name for resolution
public sealed record IrLoadField(string DeclaringType, string FieldName, SourceSpan Span) : IrInstruction(Span);
public sealed record IrStoreField(string DeclaringType, string FieldName, SourceSpan Span) : IrInstruction(Span);

// Arithmetic
public sealed record IrBinaryOp(IrBinaryOpKind Op, CulebralType? OperandType, SourceSpan Span) : IrInstruction(Span);
public sealed record IrUnaryOp(IrUnaryOpKind Op, SourceSpan Span) : IrInstruction(Span);

public enum IrBinaryOpKind
{
    Add, Sub, Mul, Div, IntDiv, Mod, Pow,
    BitAnd, BitOr, BitXor, ShiftLeft, ShiftRight,
    Equal, NotEqual, LessThan, GreaterThan, LessEqual, GreaterEqual,
    LogicalAnd, LogicalOr,
}

public enum IrUnaryOpKind
{
    Negate, BitNot, LogicalNot,
}

// Control flow
public sealed record IrBranch(string TargetLabel, SourceSpan Span) : IrInstruction(Span);
public sealed record IrBranchIf(string TrueLabel, string FalseLabel, SourceSpan Span) : IrInstruction(Span);
public sealed record IrReturn(bool HasValue, SourceSpan Span) : IrInstruction(Span);

// Calls
public sealed record IrCall(string FunctionName, int ArgCount, bool IsStatic, SourceSpan Span) : IrInstruction(Span);
public sealed record IrCallVirtual(string MethodName, int ArgCount, SourceSpan Span) : IrInstruction(Span);
public sealed record IrCallBuiltin(string Name, int ArgCount, SourceSpan Span) : IrInstruction(Span);
/// <summary>Call an instance method on a user-defined type. Receiver is already on the stack.</summary>
public sealed record IrCallMethod(string DeclaringType, string MethodName, int ArgCount, SourceSpan Span) : IrInstruction(Span);

// Object operations
public sealed record IrNewObj(string TypeName, int ArgCount, SourceSpan Span) : IrInstruction(Span);
public sealed record IrCastClass(string TypeName, SourceSpan Span) : IrInstruction(Span);
public sealed record IrIsInst(string TypeName, SourceSpan Span) : IrInstruction(Span);
public sealed record IrBox(CulebralType Type, SourceSpan Span) : IrInstruction(Span);
public sealed record IrUnbox(CulebralType Type, SourceSpan Span) : IrInstruction(Span);

// Array / collection
public sealed record IrNewArray(CulebralType ElementType, int Length, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadElement(SourceSpan Span) : IrInstruction(Span);
public sealed record IrStoreElement(SourceSpan Span) : IrInstruction(Span);

// Stack
public sealed record IrDup(SourceSpan Span) : IrInstruction(Span);
public sealed record IrPop(SourceSpan Span) : IrInstruction(Span);
public sealed record IrNop(SourceSpan Span) : IrInstruction(Span);

// String formatting
public sealed record IrStringConcat(int PartCount, SourceSpan Span) : IrInstruction(Span);
public sealed record IrToString(CulebralType SourceType, SourceSpan Span) : IrInstruction(Span);

// Comparison helpers
public sealed record IrCompareNull(bool BranchIfNull, string TargetLabel, SourceSpan Span) : IrInstruction(Span);
public sealed record IrIsNull(bool Negated, SourceSpan Span) : IrInstruction(Span);
public sealed record IrThrow(SourceSpan Span) : IrInstruction(Span);
public sealed record IrBeginExceptionBlock(string HandlerLabel, SourceSpan Span) : IrInstruction(Span);
public sealed record IrEndExceptionBlock(SourceSpan Span) : IrInstruction(Span);
public sealed record IrBeginCatchBlock(Type ExceptionType, SourceSpan Span) : IrInstruction(Span);
public sealed record IrBeginFinallyBlock(SourceSpan Span) : IrInstruction(Span);

// .NET Interop — carry resolved System.Type for direct reflection-based emission
public sealed record IrCallDotNetStatic(Type DeclaringType, string MethodName, int ArgCount, SourceSpan Span) : IrInstruction(Span);
public sealed record IrCallDotNetInstance(Type DeclaringType, string MethodName, int ArgCount, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadDotNetProperty(Type DeclaringType, string PropertyName, bool IsStatic, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadDotNetField(Type DeclaringType, string FieldName, bool IsStatic, SourceSpan Span) : IrInstruction(Span);
public sealed record IrNewDotNetObj(Type Type, int ArgCount, SourceSpan Span) : IrInstruction(Span);

// .NET Generic method calls — carry type arguments for MakeGenericMethod
public sealed record IrCallDotNetGenericStatic(Type DeclaringType, string MethodName, int ArgCount, Type[] TypeArguments, SourceSpan Span) : IrInstruction(Span);
public sealed record IrCallDotNetGenericInstance(Type DeclaringType, string MethodName, int ArgCount, Type[] TypeArguments, SourceSpan Span) : IrInstruction(Span);

// Extension method calls — static methods called with receiver as first argument
public sealed record IrCallExtensionMethod(Type DeclaringType, string MethodName, int ArgCount, Type[]? TypeArguments, SourceSpan Span) : IrInstruction(Span);

// Lambda / delegate creation — creates a delegate pointing to a generated static method
public sealed record IrCreateDelegate(string MethodName, int ParamCount, SourceSpan Span) : IrInstruction(Span);

// Delegate invocation — stack has: [delegate, arg0, arg1, ..., argN]
public sealed record IrInvokeDelegate(int ArgCount, SourceSpan Span) : IrInstruction(Span);

// Slicing — stack has: [object, start?, stop?, step?] depending on flags
public sealed record IrSlice(bool HasStart, bool HasStop, bool HasStep, SourceSpan Span) : IrInstruction(Span);

// Array creation from values already on the stack
public sealed record IrNewArrayFromStack(int Count, SourceSpan Span) : IrInstruction(Span);

// Generator yield — adds the value on the stack to the generator list
public sealed record IrYield(SourceSpan Span) : IrInstruction(Span);

// Python-compatible print() — positional args are on the stack, named args are compile-time constants
public sealed record IrPrint(int PositionalArgCount, string? Sep, string? End, bool Flush, bool UseStderr, SourceSpan Span) : IrInstruction(Span);
