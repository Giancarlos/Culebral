using Skelvon.Compiler.Diagnostics;
using Skelvon.Compiler.Semantics;

namespace Skelvon.Compiler.IR;

// ─── Module (Top-Level Container) ───

/// <summary>
/// A compiled module — the IR for one .skv file.
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
    public required SkelvonType Type { get; init; }
    public IrInstruction? DefaultValue { get; init; }
    public bool IsStatic { get; init; }
}

public sealed class IrProperty
{
    public required string Name { get; init; }
    public required SkelvonType Type { get; init; }
    public IrFunction? Getter { get; init; }
    public IrFunction? Setter { get; init; }
}

// ─── Functions ───

public sealed class IrFunction
{
    public required string Name { get; init; }
    public required SkelvonType ReturnType { get; init; }
    public required List<IrParameter> Parameters { get; init; }
    public required List<IrBasicBlock> Body { get; init; }
    public bool IsStatic { get; init; } = true;
    public bool IsAsync { get; init; }
    public bool IsEntryPoint { get; init; }
    public string? DeclaringType { get; init; }

    /// <summary>Local variable declarations, in order of first use.</summary>
    public List<IrLocal> Locals { get; } = [];
}

public sealed class IrParameter
{
    public required string Name { get; init; }
    public required SkelvonType Type { get; init; }
    public int Index { get; init; }
}

public sealed class IrLocal
{
    public required string Name { get; init; }
    public required SkelvonType Type { get; init; }
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
public sealed record IrLoadField(string FieldName, SourceSpan Span) : IrInstruction(Span);
public sealed record IrStoreField(string FieldName, SourceSpan Span) : IrInstruction(Span);

// Arithmetic
public sealed record IrBinaryOp(IrBinaryOpKind Op, SkelvonType? OperandType, SourceSpan Span) : IrInstruction(Span);
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

// Object operations
public sealed record IrNewObj(string TypeName, int ArgCount, SourceSpan Span) : IrInstruction(Span);
public sealed record IrCastClass(string TypeName, SourceSpan Span) : IrInstruction(Span);
public sealed record IrIsInst(string TypeName, SourceSpan Span) : IrInstruction(Span);
public sealed record IrBox(SkelvonType Type, SourceSpan Span) : IrInstruction(Span);
public sealed record IrUnbox(SkelvonType Type, SourceSpan Span) : IrInstruction(Span);

// Array / collection
public sealed record IrNewArray(SkelvonType ElementType, int Length, SourceSpan Span) : IrInstruction(Span);
public sealed record IrLoadElement(SourceSpan Span) : IrInstruction(Span);
public sealed record IrStoreElement(SourceSpan Span) : IrInstruction(Span);

// Stack
public sealed record IrDup(SourceSpan Span) : IrInstruction(Span);
public sealed record IrPop(SourceSpan Span) : IrInstruction(Span);
public sealed record IrNop(SourceSpan Span) : IrInstruction(Span);

// String formatting
public sealed record IrStringConcat(int PartCount, SourceSpan Span) : IrInstruction(Span);
public sealed record IrToString(SkelvonType SourceType, SourceSpan Span) : IrInstruction(Span);

// Comparison helpers
public sealed record IrCompareNull(bool BranchIfNull, string TargetLabel, SourceSpan Span) : IrInstruction(Span);
