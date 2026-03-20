using Culebral.Compiler.Diagnostics;

namespace Culebral.Compiler.Parser;

// ─── Base Node ───

public abstract record AstNode(SourceSpan Span);

// ─── Top-Level ───

public sealed record CompilationUnit(
    List<AstNode> Statements,
    SourceSpan Span) : AstNode(Span);

// ─── Statements ───

public abstract record Statement(SourceSpan Span) : AstNode(Span);

public sealed record ExpressionStatement(
    Expression Expr,
    SourceSpan Span) : Statement(Span);

public sealed record ReturnStatement(
    Expression? Value,
    SourceSpan Span) : Statement(Span);

public sealed record YieldStatement(
    Expression? Value,
    SourceSpan Span) : Statement(Span);

public sealed record BreakStatement(SourceSpan Span) : Statement(Span);

public sealed record ContinueStatement(SourceSpan Span) : Statement(Span);

public sealed record PassStatement(SourceSpan Span) : Statement(Span);

public sealed record RaiseStatement(
    Expression? Value,
    SourceSpan Span) : Statement(Span);

public sealed record AssertStatement(
    Expression Condition,
    Expression? Message,
    SourceSpan Span) : Statement(Span);

/// <summary>
/// A compound statement that holds multiple statements desugared from a single source construct
/// (e.g., chained assignment a = b = c = 0 → c = 0; b = c; a = b).
/// </summary>
public sealed record CompoundStatement(
    List<Statement> Statements,
    SourceSpan Span) : Statement(Span);

// ─── Variable Declaration / Assignment ───

public sealed record AssignmentStatement(
    Expression Target,
    Expression Value,
    SourceSpan Span) : Statement(Span);

public sealed record AugmentedAssignmentStatement(
    Expression Target,
    Lexer.TokenKind Op,
    Expression Value,
    SourceSpan Span) : Statement(Span);

public sealed record AnnotatedAssignment(
    string Name,
    TypeAnnotation TypeAnnotation,
    Expression? Value,
    SourceSpan Span) : Statement(Span);

// ─── Control Flow ───

public sealed record IfStatement(
    Expression Condition,
    Block Body,
    List<ElifClause> Elifs,
    Block? ElseBody,
    SourceSpan Span) : Statement(Span);

public sealed record ElifClause(
    Expression Condition,
    Block Body,
    SourceSpan Span) : AstNode(Span);

public sealed record WhileStatement(
    Expression Condition,
    Block Body,
    Block? ElseBody,
    SourceSpan Span) : Statement(Span);

public sealed record ForStatement(
    string Variable,
    Expression Iterable,
    Block Body,
    Block? ElseBody,
    SourceSpan Span) : Statement(Span);

public sealed record WithStatement(
    List<WithItem> Items,
    Block Body,
    SourceSpan Span) : Statement(Span);

public sealed record WithItem(
    Expression ContextExpr,
    string? Variable,
    SourceSpan Span) : AstNode(Span);

public sealed record TryStatement(
    Block Body,
    List<ExceptClause> ExceptClauses,
    Block? FinallyBody,
    SourceSpan Span) : Statement(Span);

public sealed record ExceptClause(
    TypeAnnotation? ExceptionType,
    string? Variable,
    Block Body,
    SourceSpan Span) : AstNode(Span);

// ─── Match Statement ───

public sealed record MatchStatement(
    Expression Subject,
    List<MatchCase> Cases,
    SourceSpan Span) : Statement(Span);

public sealed record MatchCase(
    Pattern Pattern,
    Expression? Guard,
    Block Body,
    SourceSpan Span) : AstNode(Span);

// ─── Patterns ───

public abstract record Pattern(SourceSpan Span) : AstNode(Span);

public sealed record WildcardPattern(SourceSpan Span) : Pattern(Span);

public sealed record NamePattern(string Name, SourceSpan Span) : Pattern(Span);

public sealed record LiteralPattern(Expression Literal, SourceSpan Span) : Pattern(Span);

public sealed record TypePattern(
    string TypeName,
    string? BindingName,
    SourceSpan Span) : Pattern(Span);

public sealed record ConstructorPattern(
    string TypeName,
    List<Pattern> Fields,
    SourceSpan Span) : Pattern(Span);

public sealed record NonePattern(SourceSpan Span) : Pattern(Span);

// ─── Import ───

public sealed record ImportStatement(
    string ModulePath,
    string? Alias,
    SourceSpan Span) : Statement(Span);

public sealed record FromImportStatement(
    string ModulePath,
    List<ImportName> Names,
    SourceSpan Span) : Statement(Span);

public sealed record ImportName(
    string Name,
    string? Alias,
    SourceSpan Span) : AstNode(Span);

// ─── Function Definition ───

public sealed record FunctionDef(
    string Name,
    List<Parameter> Parameters,
    TypeAnnotation? ReturnType,
    Block Body,
    bool IsAsync,
    List<Decorator> Decorators,
    SourceSpan Span) : Statement(Span);

public sealed record Parameter(
    string Name,
    TypeAnnotation Type,
    Expression? Default,
    bool IsVarArgs,
    SourceSpan Span) : AstNode(Span);

public sealed record Decorator(
    Expression Expr,
    SourceSpan Span) : AstNode(Span);

// ─── Class / Struct / Record / Enum / Interface ───

public sealed record ClassDef(
    string Name,
    List<TypeParameter>? TypeParameters,
    List<TypeAnnotation> Bases,
    List<AstNode> Members,
    SourceSpan Span) : Statement(Span);

public sealed record StructDef(
    string Name,
    List<TypeParameter>? TypeParameters,
    List<AstNode> Members,
    SourceSpan Span) : Statement(Span);

public sealed record RecordDef(
    string Name,
    List<TypeParameter>? TypeParameters,
    List<AstNode> Members,
    SourceSpan Span) : Statement(Span);

public sealed record EnumDef(
    string Name,
    List<EnumVariant> Variants,
    SourceSpan Span) : Statement(Span);

public sealed record EnumVariant(
    string Name,
    List<Parameter>? Fields,
    SourceSpan Span) : AstNode(Span);

public sealed record InterfaceDef(
    string Name,
    List<TypeParameter>? TypeParameters,
    List<AstNode> Members,
    SourceSpan Span) : Statement(Span);

public sealed record PropertyDef(
    string Name,
    TypeAnnotation ReturnType,
    Block? Getter,
    Block? Setter,
    SourceSpan Span) : AstNode(Span);

public sealed record FieldDeclaration(
    string Name,
    TypeAnnotation Type,
    Expression? Default,
    SourceSpan Span) : AstNode(Span);

// ─── Type Annotations ───

public abstract record TypeAnnotation(SourceSpan Span) : AstNode(Span);

public sealed record SimpleType(string Name, SourceSpan Span) : TypeAnnotation(Span);

public sealed record NullableType(TypeAnnotation Inner, SourceSpan Span) : TypeAnnotation(Span);

public sealed record GenericType(
    string Name,
    List<TypeAnnotation> TypeArgs,
    SourceSpan Span) : TypeAnnotation(Span);

public sealed record TupleType(
    List<TupleTypeElement> Elements,
    SourceSpan Span) : TypeAnnotation(Span);

public sealed record TupleTypeElement(
    string? Name,
    TypeAnnotation Type,
    SourceSpan Span) : AstNode(Span);

public sealed record TypeParameter(
    string Name,
    TypeAnnotation? Constraint,
    SourceSpan Span) : AstNode(Span);

// ─── Type Alias ───

public sealed record TypeAliasStatement(
    string Name,
    List<TypeParameter>? TypeParams,
    TypeAnnotation Target,
    SourceSpan Span) : Statement(Span);

// ─── When Target (Conditional Compilation) ───

public sealed record WhenStatement(
    string Target,
    string Comparison,
    string Value,
    Block Body,
    SourceSpan Span) : Statement(Span);

// ─── Expressions ───

public abstract record Expression(SourceSpan Span) : AstNode(Span);

public sealed record IntLiteralExpr(long Value, SourceSpan Span) : Expression(Span);

public sealed record FloatLiteralExpr(double Value, SourceSpan Span) : Expression(Span);

public sealed record StringLiteralExpr(string Value, SourceSpan Span) : Expression(Span);

public sealed record FStringExpr(
    List<FStringPart> Parts,
    SourceSpan Span) : Expression(Span);

public abstract record FStringPart(SourceSpan Span) : AstNode(Span);
public sealed record FStringText(string Text, SourceSpan Span) : FStringPart(Span);
public sealed record FStringInterpolation(Expression Expr, SourceSpan Span) : FStringPart(Span);

public sealed record BoolLiteralExpr(bool Value, SourceSpan Span) : Expression(Span);

public sealed record NoneLiteralExpr(SourceSpan Span) : Expression(Span);

public sealed record IdentifierExpr(string Name, SourceSpan Span) : Expression(Span);

public sealed record FieldAccessExpr(string FieldName, SourceSpan Span) : Expression(Span);

public sealed record BinaryExpr(
    Expression Left,
    Lexer.TokenKind Op,
    Expression Right,
    SourceSpan Span) : Expression(Span);

public sealed record UnaryExpr(
    Lexer.TokenKind Op,
    Expression Operand,
    SourceSpan Span) : Expression(Span);

public sealed record CallExpr(
    Expression Callee,
    List<Argument> Arguments,
    SourceSpan Span) : Expression(Span);

public sealed record Argument(
    string? Name,
    Expression Value,
    bool IsUnpacked,
    SourceSpan Span) : AstNode(Span);

public sealed record MemberAccessExpr(
    Expression Object,
    string Member,
    SourceSpan Span) : Expression(Span);

public sealed record IndexExpr(
    Expression Object,
    Expression Index,
    SourceSpan Span) : Expression(Span);

public sealed record SliceExpr(
    Expression Object,
    Expression? Lower,
    Expression? Upper,
    Expression? Step,
    SourceSpan Span) : Expression(Span);

public sealed record ListExpr(
    List<Expression> Elements,
    SourceSpan Span) : Expression(Span);

public sealed record DictExpr(
    List<(Expression Key, Expression Value)> Entries,
    SourceSpan Span) : Expression(Span);

public sealed record SetExpr(
    List<Expression> Elements,
    SourceSpan Span) : Expression(Span);

public sealed record TupleExpr(
    List<Expression> Elements,
    SourceSpan Span) : Expression(Span);

public sealed record LambdaExpr(
    List<Parameter> Parameters,
    Expression Body,
    SourceSpan Span) : Expression(Span);

public sealed record ConditionalExpr(
    Expression Condition,
    Expression TrueExpr,
    Expression FalseExpr,
    SourceSpan Span) : Expression(Span);

public sealed record AwaitExpr(
    Expression Operand,
    SourceSpan Span) : Expression(Span);

public sealed record IsExpr(
    Expression Left,
    TypeAnnotation Type,
    bool Negated,
    SourceSpan Span) : Expression(Span);

public sealed record InExpr(
    Expression Left,
    Expression Right,
    bool Negated,
    SourceSpan Span) : Expression(Span);

public sealed record ListComprehension(
    Expression Element,
    string Variable,
    Expression Iterable,
    Expression? Condition,
    SourceSpan Span) : Expression(Span);

public sealed record DictComprehension(
    Expression Key,
    Expression Value,
    string Variable,
    Expression Iterable,
    Expression? Condition,
    SourceSpan Span) : Expression(Span);

public sealed record GeneratorExpr(
    Expression Element,
    string Variable,
    Expression Iterable,
    Expression? Condition,
    SourceSpan Span) : Expression(Span);

public sealed record WithExpr(
    Expression Source,
    List<(string Name, Expression Value)> Updates,
    SourceSpan Span) : Expression(Span);

public sealed record TypeCastExpr(
    Expression Expr,
    TypeAnnotation Type,
    SourceSpan Span) : Expression(Span);

// ─── Block (indented body) ───

public sealed record Block(
    List<Statement> Statements,
    SourceSpan Span) : AstNode(Span);
