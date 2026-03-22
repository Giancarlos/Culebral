using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Parser;

namespace Culebral.Compiler.Semantics;

/// <summary>
/// Two-pass type checker:
///   Pass 1: Collect all top-level declarations (functions, classes, etc.)
///   Pass 2: Check function bodies, infer local types, verify constraints.
/// </summary>
public sealed class TypeChecker
{
    private readonly DiagnosticBag _diagnostics;
    private SymbolScope _currentScope;
    private readonly Dictionary<AstNode, CulebralType> _resolvedTypes = new();
    private string? _currentClassName;
    private readonly HashSet<string> _knownTypeParams = new();
    private readonly DotNetTypeResolver _dotNetResolver = new();

    /// <summary>
    /// Flow-sensitive type narrowing. When an <c>if x is None: return</c> guard narrows
    /// a nullable variable, the narrowed (non-null) type is stored here and consulted
    /// during identifier resolution. Entries are added/removed as the checker walks
    /// control flow — they are scoped and temporary.
    /// </summary>
    private readonly Dictionary<string, CulebralType> _narrowedTypes = new();

    /// <summary>
    /// Tracks generic type parameter constraints for user-defined types.
    /// Key: "TypeName.ParamName", Value: constraint type (interface or class).
    /// </summary>
    private readonly Dictionary<string, CulebralType> _typeParamConstraints = new();

    /// <summary>
    /// Tracks the ordered list of type parameter names per user-defined type.
    /// Key: type name, Value: list of type parameter names.
    /// </summary>
    private readonly Dictionary<string, List<string>> _typeParamNames = new();

    public TypeChecker(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
        _currentScope = BuiltinSymbols.CreateGlobalScope();
    }

    public DotNetTypeResolver DotNetResolver => _dotNetResolver;

    public IReadOnlyDictionary<AstNode, CulebralType> ResolvedTypes => _resolvedTypes;
    public SymbolScope GlobalScope => _currentScope;

    public void Check(CompilationUnit unit)
    {
        // Pass 1: Declare all top-level names
        foreach (var stmt in unit.Statements)
            DeclareTopLevel(stmt);

        // Pass 2: Check bodies
        foreach (var stmt in unit.Statements)
            CheckNode(stmt);
    }

    // ─── Pass 1: Declarations ───

    private void DeclareTopLevel(AstNode node)
    {
        switch (node)
        {
            case FunctionDef func:
                DeclareFunctionSymbol(func);
                break;
            case ClassDef cls:
                DeclareClassSymbol(cls);
                break;
            case StructDef strct:
                _currentScope.TryDeclare(new Symbol
                {
                    Name = strct.Name,
                    Kind = SymbolKind.Type,
                    Type = new StructType(strct.Name, strct.Name),
                });
                RegisterTypeParameters(strct.Name, strct.TypeParameters);
                break;
            case RecordDef rec:
                _currentScope.TryDeclare(new Symbol
                {
                    Name = rec.Name,
                    Kind = SymbolKind.Type,
                    Type = new RecordType(rec.Name, rec.Name),
                });
                RegisterTypeParameters(rec.Name, rec.TypeParameters);
                break;
            case EnumDef enumDef:
                DeclareEnumSymbol(enumDef);
                break;
            case InterfaceDef iface:
                _currentScope.TryDeclare(new Symbol
                {
                    Name = iface.Name,
                    Kind = SymbolKind.Type,
                    Type = new InterfaceType(iface.Name, iface.Name),
                });
                RegisterTypeParameters(iface.Name, iface.TypeParameters);
                break;
            case TypeAliasStatement alias:
                _currentScope.TryDeclare(new Symbol
                {
                    Name = alias.Name,
                    Kind = SymbolKind.Type,
                    Type = ResolveTypeAnnotation(alias.Target),
                });
                break;
        }
    }

    private void DeclareFunctionSymbol(FunctionDef func)
    {
        var paramTypes = func.Parameters.Select(p => ResolveTypeAnnotation(p.Type)).ToArray();
        var returnType = func.ReturnType is not null
            ? ResolveTypeAnnotation(func.ReturnType)
            : PrimitiveType.Void;

        _currentScope.TryDeclare(new Symbol
        {
            Name = func.Name,
            Kind = SymbolKind.Function,
            Type = new FunctionType(paramTypes, returnType),
            IsMutable = false,
            IsAsync = func.IsAsync,
        });
    }

    private void DeclareClassSymbol(ClassDef cls)
    {
        var classType = new ClassType(cls.Name, cls.Name);
        _currentScope.TryDeclare(new Symbol
        {
            Name = cls.Name,
            Kind = SymbolKind.Type,
            Type = classType,
        });
        RegisterTypeParameters(cls.Name, cls.TypeParameters);
    }

    private void DeclareEnumSymbol(EnumDef enumDef)
    {
        var enumType = new EnumType(enumDef.Name, enumDef.Name);
        _currentScope.TryDeclare(new Symbol
        {
            Name = enumDef.Name,
            Kind = SymbolKind.Type,
            Type = enumType,
        });

        // Declare each variant as a constructor
        foreach (var variant in enumDef.Variants)
        {
            var variantName = $"{enumDef.Name}.{variant.Name}";
            if (variant.Fields is { Count: > 0 })
            {
                var paramTypes = variant.Fields.Select(f => ResolveTypeAnnotation(f.Type)).ToArray();
                _currentScope.TryDeclare(new Symbol
                {
                    Name = variantName,
                    Kind = SymbolKind.EnumVariant,
                    Type = new FunctionType(paramTypes, enumType),
                });
            }
            else
            {
                _currentScope.TryDeclare(new Symbol
                {
                    Name = variantName,
                    Kind = SymbolKind.EnumVariant,
                    Type = enumType,
                });
            }
        }
    }

    // ─── Pass 2: Checking ───

    private void CheckNode(AstNode node)
    {
        switch (node)
        {
            case FunctionDef func:
                CheckFunction(func);
                break;
            case ClassDef cls:
                CheckClass(cls);
                break;
            case StructDef strct:
                CheckStruct(strct);
                break;
            case RecordDef rec:
                CheckRecord(rec);
                break;
            case InterfaceDef iface:
                CheckInterface(iface);
                break;
            case TypeAliasStatement:
                break; // Already handled in pass 1
            case Statement stmt:
                CheckStatement(stmt);
                break;
        }
    }

    private void CheckFunction(FunctionDef func)
    {
        var funcScope = _currentScope.CreateChild(func.Name);

        // Declare parameters
        foreach (var param in func.Parameters)
        {
            var paramType = ResolveTypeAnnotation(param.Type);
            funcScope.TryDeclare(new Symbol
            {
                Name = param.Name,
                Kind = SymbolKind.Parameter,
                Type = paramType,
                IsMutable = true,
                DeclaringScope = func.Name,
            });
        }

        var prevScope = _currentScope;
        var prevNarrowings = new Dictionary<string, CulebralType>(_narrowedTypes);
        _currentScope = funcScope;
        CheckBlock(func.Body);
        _currentScope = prevScope;
        // Restore narrowings — function body narrowings should not leak out
        _narrowedTypes.Clear();
        foreach (var kv in prevNarrowings)
            _narrowedTypes[kv.Key] = kv.Value;
    }

    private void CheckClass(ClassDef cls)
    {
        var classScope = _currentScope.CreateChild(cls.Name);
        var prevClassName = _currentClassName;
        _currentClassName = cls.Name;

        // Register type parameters (T, U, etc.) in scope
        if (cls.TypeParameters is not null)
        {
            foreach (var tp in cls.TypeParameters)
            {
                _knownTypeParams.Add(tp.Name);
                classScope.TryDeclare(new Symbol
                {
                    Name = tp.Name,
                    Kind = SymbolKind.Type,
                    Type = new TypeParameterType(tp.Name),
                    IsMutable = false,
                });
            }
        }

        // Switch to class scope BEFORE resolving field types (so T is visible)
        var prevScope = _currentScope;
        _currentScope = classScope;

        // Declare fields
        foreach (var member in cls.Members)
        {
            if (member is FieldDeclaration field)
            {
                var fieldType = ResolveTypeAnnotation(field.Type);
                classScope.TryDeclare(new Symbol
                {
                    Name = field.Name,
                    Kind = SymbolKind.Field,
                    Type = fieldType,
                    DeclaringScope = cls.Name,
                });
            }
        }

        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case FunctionDef method:
                    DeclareFunctionSymbol(method);
                    CheckFunction(method);
                    break;
                case FieldDeclaration field when field.Default is not null:
                    InferType(field.Default);
                    break;
            }
        }

        _currentScope = prevScope;
        _currentClassName = prevClassName;
    }

    private void CheckStruct(StructDef strct)
    {
        var scope = _currentScope.CreateChild(strct.Name);
        var prevScope = _currentScope;
        var prevClassName = _currentClassName;
        _currentClassName = strct.Name;
        _currentScope = scope;

        if (strct.TypeParameters is not null)
            foreach (var tp in strct.TypeParameters)
                scope.TryDeclare(new Symbol { Name = tp.Name, Kind = SymbolKind.Type, Type = new TypeParameterType(tp.Name), IsMutable = false });

        foreach (var member in strct.Members)
        {
            if (member is FieldDeclaration field)
            {
                var fieldType = ResolveTypeAnnotation(field.Type);
                scope.TryDeclare(new Symbol
                {
                    Name = field.Name,
                    Kind = SymbolKind.Field,
                    Type = fieldType,
                    DeclaringScope = strct.Name,
                });
            }
            else if (member is FunctionDef method)
            {
                DeclareFunctionSymbol(method);
                CheckFunction(method);
            }
        }

        _currentScope = prevScope;
        _currentClassName = prevClassName;
    }

    private void CheckRecord(RecordDef rec)
    {
        var scope = _currentScope.CreateChild(rec.Name);
        var prevScope = _currentScope;
        var prevClassName = _currentClassName;
        _currentClassName = rec.Name;
        _currentScope = scope;

        if (rec.TypeParameters is not null)
            foreach (var tp in rec.TypeParameters)
                scope.TryDeclare(new Symbol { Name = tp.Name, Kind = SymbolKind.Type, Type = new TypeParameterType(tp.Name), IsMutable = false });

        foreach (var member in rec.Members)
        {
            if (member is FieldDeclaration field)
            {
                var fieldType = ResolveTypeAnnotation(field.Type);
                scope.TryDeclare(new Symbol
                {
                    Name = field.Name,
                    Kind = SymbolKind.Field,
                    Type = fieldType,
                    DeclaringScope = rec.Name,
                });
            }
            else if (member is FunctionDef method)
            {
                DeclareFunctionSymbol(method);
                CheckFunction(method);
            }
        }

        _currentScope = prevScope;
        _currentClassName = prevClassName;
    }

    private void CheckInterface(InterfaceDef iface)
    {
        var scope = _currentScope.CreateChild(iface.Name);
        var prevScope = _currentScope;
        _currentScope = scope;

        foreach (var member in iface.Members)
        {
            if (member is FunctionDef method)
                DeclareFunctionSymbol(method);
        }

        _currentScope = prevScope;
    }

    // ─── Statement Checking ───

    private void CheckBlock(Block block)
    {
        foreach (var stmt in block.Statements)
            CheckStatement(stmt);
    }

    private void CheckStatement(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStatement exprStmt:
                InferType(exprStmt.Expr);
                break;

            case ReturnStatement ret:
                if (ret.Value is not null)
                    InferType(ret.Value);
                break;

            case CompoundStatement compound:
                foreach (var inner in compound.Statements)
                    CheckStatement(inner);
                break;

            case AssignmentStatement assign:
                CheckAssignment(assign);
                break;

            case AnnotatedAssignment annotated:
                CheckAnnotatedAssignment(annotated);
                break;

            case AugmentedAssignmentStatement augAssign:
                InferType(augAssign.Target);
                InferType(augAssign.Value);
                break;

            case IfStatement ifStmt:
                CheckIfStatement(ifStmt);
                break;

            case WhileStatement whileStmt:
                InferType(whileStmt.Condition);
                CheckBlock(whileStmt.Body);
                if (whileStmt.ElseBody is not null)
                    CheckBlock(whileStmt.ElseBody);
                break;

            case ForStatement forStmt:
                CheckForStatement(forStmt);
                break;

            case WithStatement withStmt:
                CheckWithStatement(withStmt);
                break;

            case MatchStatement matchStmt:
                InferType(matchStmt.Subject);
                foreach (var c in matchStmt.Cases)
                    CheckBlock(c.Body);
                break;

            case TryStatement tryStmt:
                CheckBlock(tryStmt.Body);
                foreach (var exc in tryStmt.ExceptClauses)
                    CheckBlock(exc.Body);
                if (tryStmt.FinallyBody is not null)
                    CheckBlock(tryStmt.FinallyBody);
                break;

            case FunctionDef func:
                DeclareFunctionSymbol(func);
                CheckFunction(func);
                break;

            case ClassDef cls:
                DeclareClassSymbol(cls);
                CheckClass(cls);
                break;

            case FromImportStatement fromImport:
                CheckFromImport(fromImport);
                break;

            case ImportStatement import:
                CheckImport(import);
                break;

            case AssertStatement assertStmt:
                InferType(assertStmt.Condition);
                if (assertStmt.Message is not null)
                    InferType(assertStmt.Message);
                break;

            case WhenStatement:
            case BreakStatement:
            case ContinueStatement:
            case PassStatement:
            case YieldStatement:
            case RaiseStatement:
                break;
        }
    }

    private void CheckIfStatement(IfStatement ifStmt)
    {
        InferType(ifStmt.Condition);

        // Detect "if x is None" / "if x is not None" pattern for flow narrowing
        var (narrowVar, narrowType, isNegated) = ExtractNullCheck(ifStmt.Condition);

        if (narrowVar is not null && narrowType is not null)
        {
            if (!isNegated)
            {
                // Pattern: if x is None: <body>
                // Inside the body, x is still nullable (it IS None).
                // If the body ends with an early exit, narrow x after the if.
                CheckBlock(ifStmt.Body);

                foreach (var elif in ifStmt.Elifs)
                {
                    InferType(elif.Condition);
                    CheckBlock(elif.Body);
                }
                if (ifStmt.ElseBody is not null)
                    CheckBlock(ifStmt.ElseBody);

                // If the if-body has an early exit, narrow x in the continuation
                if (BlockHasEarlyExit(ifStmt.Body))
                    _narrowedTypes[narrowVar] = narrowType;
            }
            else
            {
                // Pattern: if x is not None: <body>
                // Inside the body, x is narrowed to non-null.
                _narrowedTypes[narrowVar] = narrowType;
                CheckBlock(ifStmt.Body);
                _narrowedTypes.Remove(narrowVar);

                foreach (var elif in ifStmt.Elifs)
                {
                    InferType(elif.Condition);
                    CheckBlock(elif.Body);
                }
                if (ifStmt.ElseBody is not null)
                    CheckBlock(ifStmt.ElseBody);
            }
        }
        else
        {
            // Not a null-check pattern — standard checking
            CheckBlock(ifStmt.Body);
            foreach (var elif in ifStmt.Elifs)
            {
                InferType(elif.Condition);
                CheckBlock(elif.Body);
            }
            if (ifStmt.ElseBody is not null)
                CheckBlock(ifStmt.ElseBody);
        }
    }

    /// <summary>
    /// Extracts null-check info from an <c>is None</c> / <c>is not None</c> condition.
    /// Returns (variableName, innerNonNullType, isNegated) if the pattern matches,
    /// or (null, null, false) otherwise.
    /// </summary>
    private (string? VarName, CulebralType? InnerType, bool Negated) ExtractNullCheck(Expression condition)
    {
        if (condition is IsExpr { Left: IdentifierExpr ident, Type: SimpleType { Name: "None" } } isExpr)
        {
            var symbol = _currentScope.Lookup(ident.Name);
            if (symbol?.Type is NullableCulebralType nullable)
                return (ident.Name, nullable.Inner, isExpr.Negated);
        }
        return (null, null, false);
    }

    /// <summary>
    /// Returns true if the block ends with an early-exit statement (return, raise, break, continue).
    /// </summary>
    private static bool BlockHasEarlyExit(Block block)
    {
        if (block.Statements.Count == 0)
            return false;

        return block.Statements[^1] is ReturnStatement or RaiseStatement or BreakStatement or ContinueStatement;
    }

    private void CheckAssignment(AssignmentStatement assign)
    {
        var valueType = InferType(assign.Value);

        if (assign.Target is IdentifierExpr ident)
        {
            var existing = _currentScope.LookupLocal(ident.Name);
            if (existing is null)
            {
                // New variable — infer type from value
                _currentScope.TryDeclare(new Symbol
                {
                    Name = ident.Name,
                    Kind = SymbolKind.Variable,
                    Type = valueType,
                });
            }
            else
            {
                // Reassignment — check type compatibility
                if (!IsAssignable(valueType, existing.Type))
                {
                    _diagnostics.Error("LEB2001",
                        $"Cannot assign {valueType.DisplayName} to variable '{ident.Name}' of type {existing.Type.DisplayName}",
                        assign.Span);
                }
            }
        }
        else if (assign.Target is TupleExpr tupleTarget)
        {
            // Tuple unpacking: a, b = b, a  /  x, y, z = 10, 20, 30
            // Resolve element types from the RHS tuple (or fall back to Object).
            CulebralType[] elementTypes;
            if (valueType is TupleCulebralType tupleValue)
            {
                elementTypes = tupleValue.Elements;
            }
            else
            {
                // RHS is a single value (e.g. function returning a tuple) — use Object for each target
                elementTypes = new CulebralType[tupleTarget.Elements.Count];
                Array.Fill(elementTypes, PrimitiveType.Object);
            }

            for (int i = 0; i < tupleTarget.Elements.Count; i++)
            {
                if (tupleTarget.Elements[i] is IdentifierExpr targetIdent)
                {
                    var elemType = i < elementTypes.Length ? elementTypes[i] : PrimitiveType.Object;
                    var existing = _currentScope.LookupLocal(targetIdent.Name);
                    if (existing is null)
                    {
                        _currentScope.TryDeclare(new Symbol
                        {
                            Name = targetIdent.Name,
                            Kind = SymbolKind.Variable,
                            Type = elemType,
                        });
                    }
                    else
                    {
                        if (!IsAssignable(elemType, existing.Type))
                        {
                            _diagnostics.Error("LEB2001",
                                $"Cannot assign {elemType.DisplayName} to variable '{targetIdent.Name}' of type {existing.Type.DisplayName}",
                                assign.Span);
                        }
                    }
                }
                else if (tupleTarget.Elements[i] is StarredExpr starred &&
                         starred.Operand is IdentifierExpr starredIdent)
                {
                    // Starred unpacking: *rest gets a list
                    var existing = _currentScope.LookupLocal(starredIdent.Name);
                    if (existing is null)
                    {
                        _currentScope.TryDeclare(new Symbol
                        {
                            Name = starredIdent.Name,
                            Kind = SymbolKind.Variable,
                            Type = PrimitiveType.Object, // List<object>
                        });
                    }
                }
                else
                {
                    // Non-identifier target (e.g. member access, index) — just type-check it
                    InferType(tupleTarget.Elements[i]);
                }
            }
        }
        else
        {
            InferType(assign.Target);
        }
    }

    private void CheckAnnotatedAssignment(AnnotatedAssignment annotated)
    {
        var declaredType = ResolveTypeAnnotation(annotated.TypeAnnotation);

        if (annotated.Value is not null)
        {
            var valueType = InferType(annotated.Value);
            if (!IsAssignable(valueType, declaredType))
            {
                _diagnostics.Error("LEB2002",
                    $"Cannot assign {valueType.DisplayName} to '{annotated.Name}' of declared type {declaredType.DisplayName}",
                    annotated.Span);
            }
        }

        _currentScope.TryDeclare(new Symbol
        {
            Name = annotated.Name,
            Kind = SymbolKind.Variable,
            Type = declaredType,
        });
    }

    private void CheckForStatement(ForStatement forStmt)
    {
        var iterableType = InferType(forStmt.Iterable);

        // Infer element type from iterable: range() → int, otherwise object
        var elementType = PrimitiveType.Object as CulebralType;
        if (forStmt.Iterable is CallExpr { Callee: IdentifierExpr { Name: "range" } })
            elementType = PrimitiveType.Int;

        // Create a child scope for the loop variable
        var loopScope = _currentScope.CreateChild("<for>");
        loopScope.TryDeclare(new Symbol
        {
            Name = forStmt.Variable,
            Kind = SymbolKind.Variable,
            Type = elementType,
        });

        var prevScope = _currentScope;
        _currentScope = loopScope;
        CheckBlock(forStmt.Body);
        _currentScope = prevScope;

        if (forStmt.ElseBody is not null)
            CheckBlock(forStmt.ElseBody);
    }

    private void CheckWithStatement(WithStatement withStmt)
    {
        // Create a child scope so that 'as' variables are visible inside the body
        var withScope = _currentScope.CreateChild("<with>");

        foreach (var item in withStmt.Items)
        {
            var contextType = InferType(item.ContextExpr);

            if (item.Variable is not null)
            {
                withScope.TryDeclare(new Symbol
                {
                    Name = item.Variable,
                    Kind = SymbolKind.Variable,
                    Type = contextType,
                });
            }
        }

        var prevScope = _currentScope;
        _currentScope = withScope;
        CheckBlock(withStmt.Body);
        _currentScope = prevScope;
    }

    // ─── .NET Import Resolution ───

    private void CheckFromImport(FromImportStatement fromImport)
    {
        var modulePath = fromImport.ModulePath;
        foreach (var name in fromImport.Names)
        {
            var fullTypeName = $"{modulePath}.{name.Name}";
            var clrType = _dotNetResolver.ResolveType(fullTypeName);
            if (clrType is not null)
            {
                var symbolName = name.Alias ?? name.Name;
                _currentScope.TryDeclare(new Symbol
                {
                    Name = symbolName,
                    Kind = SymbolKind.DotNetType,
                    Type = new DotNetType(fullTypeName, clrType),
                    IsMutable = false,
                });
            }
            else
            {
                _diagnostics.Warning("LEB2010",
                    $"Cannot resolve .NET type '{fullTypeName}'", fromImport.Span);
            }
        }
    }

    private void CheckImport(ImportStatement import)
    {
        var modulePath = import.ModulePath;

        // Try as a type first (e.g., import System.Console)
        var clrType = _dotNetResolver.ResolveType(modulePath);
        if (clrType is not null)
        {
            var symbolName = import.Alias ?? clrType.Name;
            _currentScope.TryDeclare(new Symbol
            {
                Name = symbolName,
                Kind = SymbolKind.DotNetType,
                Type = new DotNetType(modulePath, clrType),
                IsMutable = false,
            });
            return;
        }

        // Otherwise treat as a namespace alias
        var symbolName2 = import.Alias ?? modulePath.Split('.')[^1];
        _currentScope.TryDeclare(new Symbol
        {
            Name = symbolName2,
            Kind = SymbolKind.DotNetNamespace,
            Type = new DotNetNamespaceType(modulePath),
            IsMutable = false,
        });
    }

    // ─── Type Inference ───

    public CulebralType InferType(Expression expr)
    {
        var type = InferTypeCore(expr);
        _resolvedTypes[expr] = type;
        return type;
    }

    private CulebralType InferTypeCore(Expression expr)
    {
        return expr switch
        {
            IntLiteralExpr => PrimitiveType.Int,
            FloatLiteralExpr => PrimitiveType.Float,
            StringLiteralExpr => PrimitiveType.Str,
            FStringExpr fstr => InferFString(fstr),
            BoolLiteralExpr => PrimitiveType.Bool,
            NoneLiteralExpr => new NullableCulebralType(PrimitiveType.Object),

            IdentifierExpr ident => InferIdentifier(ident),
            FieldAccessExpr field => InferFieldAccess(field),

            BinaryExpr bin => InferBinary(bin),
            UnaryExpr unary => InferUnary(unary),

            CallExpr call => InferCall(call),
            MemberAccessExpr member => InferMemberAccess(member),
            IndexExpr index => InferIndex(index),
            SliceExpr => PrimitiveType.Object, // TODO: proper slice typing

            ListExpr list => InferList(list),
            DictExpr dict => InferDict(dict),
            SetExpr set => InferSet(set),
            TupleExpr tuple => InferTuple(tuple),

            LambdaExpr lambda => InferLambda(lambda),
            ConditionalExpr cond => InferConditional(cond),
            AwaitExpr await_ => InferType(await_.Operand), // Simplified
            IsExpr => PrimitiveType.Bool,
            InExpr => PrimitiveType.Bool,

            ListComprehension comp => InferListComprehension(comp),
            DictComprehension => PrimitiveType.Object,
            GeneratorExpr gen => InferGeneratorExpr(gen),
            SetComprehension => new GenericInstanceType("set", [PrimitiveType.Object], typeof(HashSet<object>)),

            TypeCastExpr cast => ResolveTypeAnnotation(cast.Type),

            StarredExpr starred => InferType(starred.Operand),

            WithExpr with_ => InferType(with_.Source),

            _ => PrimitiveType.Object,
        };
    }

    private CulebralType InferFString(FStringExpr fstr)
    {
        foreach (var part in fstr.Parts)
        {
            if (part is FStringInterpolation interp)
                InferType(interp.Expr);
        }
        return PrimitiveType.Str;
    }

    private CulebralType InferIdentifier(IdentifierExpr ident)
    {
        // Check flow-narrowed types first (e.g., after "if x is None: return")
        if (_narrowedTypes.TryGetValue(ident.Name, out var narrowed))
            return narrowed;

        // 'self' in a class method refers to the current instance (Python compatibility)
        if (ident.Name == "self" && _currentClassName is not null)
        {
            var classSymbol = _currentScope.Lookup(_currentClassName);
            if (classSymbol is not null)
                return classSymbol.Type;
            return PrimitiveType.Object;
        }

        var symbol = _currentScope.Lookup(ident.Name);
        if (symbol is null)
        {
            _diagnostics.Error("LEB2003", $"Undefined name '{ident.Name}'", ident.Span);
            return ErrorType.Instance;
        }
        return symbol.Type;
    }

    private CulebralType InferFieldAccess(FieldAccessExpr field)
    {
        // @field_name — look up in the current class scope
        var symbol = _currentScope.Lookup(field.FieldName);
        if (symbol is null)
        {
            _diagnostics.Error("LEB2004", $"Undefined field '@{field.FieldName}'", field.Span);
            return ErrorType.Instance;
        }
        return symbol.Type;
    }

    private CulebralType InferBinary(BinaryExpr bin)
    {
        var left = InferType(bin.Left);
        var right = InferType(bin.Right);

        // Comparison operators always return bool
        if (bin.Op is Lexer.TokenKind.Equal or Lexer.TokenKind.NotEqual or
            Lexer.TokenKind.LessThan or Lexer.TokenKind.GreaterThan or
            Lexer.TokenKind.LessEqual or Lexer.TokenKind.GreaterEqual or
            Lexer.TokenKind.KwAnd or Lexer.TokenKind.KwOr)
        {
            return PrimitiveType.Bool;
        }

        // String concatenation
        if (bin.Op == Lexer.TokenKind.Plus && (left == PrimitiveType.Str || right == PrimitiveType.Str))
            return PrimitiveType.Str;

        // List concatenation: list + list → list
        if (bin.Op == Lexer.TokenKind.Plus && left is GenericInstanceType { Name: "list" })
            return left;

        // List repetition: list * int → list
        if (bin.Op == Lexer.TokenKind.Star && left is GenericInstanceType { Name: "list" } && right == PrimitiveType.Int)
            return left;

        // String repetition: str * int → str
        if (bin.Op == Lexer.TokenKind.Star && left == PrimitiveType.Str && right == PrimitiveType.Int)
            return PrimitiveType.Str;

        // Dict merge: dict | dict → dict
        if (bin.Op == Lexer.TokenKind.Pipe && left is GenericInstanceType { Name: "dict" })
            return left;

        // Power operator always returns float (negative/fractional exponents produce floats)
        if (bin.Op == Lexer.TokenKind.DoubleStar)
            return PrimitiveType.Float;

        // Numeric operations
        if (left == PrimitiveType.Float || right == PrimitiveType.Float)
            return PrimitiveType.Float;

        if (left == PrimitiveType.Int && right == PrimitiveType.Int)
        {
            if (bin.Op == Lexer.TokenKind.Slash)
                return PrimitiveType.Float; // True division
            return PrimitiveType.Int;
        }

        return left; // Fallback
    }

    private CulebralType InferUnary(UnaryExpr unary)
    {
        var operandType = InferType(unary.Operand);
        if (unary.Op == Lexer.TokenKind.KwNot)
            return PrimitiveType.Bool;
        return operandType;
    }

    // Builtins with variable arg counts — skip validation
    private static readonly HashSet<string> _varArgBuiltins = [
        "print", "range", "int", "round", "min", "max", "pow", "format",
        "list", "dict", "set", "tuple", "hash", "reversed", "sorted",
        "enumerate", "zip", "map", "filter", "isinstance", "all", "any", "sum",
        "hex", "bin", "oct", "divmod", "repr", "bool", "float", "str",
        "abs", "chr", "ord", "type", "input", "len", "open",
        "assert_equal", "assert_not_equal", "cast",
    ];

    private CulebralType InferCall(CallExpr call)
    {
        var calleeType = InferType(call.Callee);

        foreach (var arg in call.Arguments)
            InferType(arg.Value);

        // Validate argument count for user-defined functions
        if (calleeType is FunctionType funcType)
        {
            if (call.Callee is IdentifierExpr callId && !_varArgBuiltins.Contains(callId.Name))
            {
                var expected = funcType.ParameterTypes.Length;
                var actual = call.Arguments.Count;
                if (actual != expected)
                    _diagnostics.Warning("LEB2020",
                        $"'{callId.Name}' expects {expected} argument(s), got {actual}",
                        call.Span);
            }
            return funcType.ReturnType;
        }

        // Constructor call — type name used as function
        if (call.Callee is IdentifierExpr ident)
        {
            var symbol = _currentScope.Lookup(ident.Name);
            if (symbol?.Kind == SymbolKind.Type)
                return symbol.Type;
            // .NET type constructor
            if (symbol?.Kind == SymbolKind.DotNetType && symbol.Type is DotNetType dnt)
                return dnt;
        }

        // Generic method call: method[TypeArg](args) → CallExpr(IndexExpr(callee, typeArg), args)
        if (call.Callee is IndexExpr { Object: MemberAccessExpr genMember, Index: var typeArgExpr })
        {
            var genObjType = _resolvedTypes.TryGetValue(genMember.Object, out var got) ? got : null;
            Type? genClrType = genObjType switch
            {
                DotNetType dt => dt.ClrBackingType,
                PrimitiveType pt => pt.ClrType,
                _ => null,
            };

            if (genClrType is not null)
            {
                // Resolve the generic method and close it with the type arg
                var genMethod = _dotNetResolver.ResolveGenericMethod(
                    genClrType, genMember.Member, call.Arguments.Count, 1, isStatic: true)
                    ?? _dotNetResolver.ResolveGenericMethod(
                    genClrType, genMember.Member, call.Arguments.Count, 1, isStatic: false);
                if (genMethod is not null)
                {
                    // Resolve the type argument
                    var typeArg = ResolveTypeArgExpr(typeArgExpr);
                    if (typeArg is not null)
                    {
                        try
                        {
                            var closed = genMethod.MakeGenericMethod(typeArg);
                            return DotNetTypeResolver.ClrTypeToCulebral(closed.ReturnType);
                        }
                        catch { /* fall through */ }
                    }
                }
            }
        }

        // .NET method call: File.read_all_text(...) or string_var.contains(...)
        if (call.Callee is MemberAccessExpr memberCall)
        {
            var objType = _resolvedTypes.TryGetValue(memberCall.Object, out var ot) ? ot : null;

            // Determine the CLR type for resolution
            Type? clrType = objType switch
            {
                DotNetType dt => dt.ClrBackingType,
                PrimitiveType pt => pt.ClrType,
                _ => null,
            };

            if (clrType is not null)
            {
                var method = _dotNetResolver.ResolveMethod(
                    clrType, memberCall.Member, call.Arguments.Count, isStatic: true)
                    ?? _dotNetResolver.ResolveMethod(
                    clrType, memberCall.Member, call.Arguments.Count, isStatic: false);
                if (method is not null)
                    return DotNetTypeResolver.ClrTypeToCulebral(method.ReturnType);
            }
        }

        return PrimitiveType.Object;
    }

    /// <summary>Resolve an expression used as a type argument (from generic call syntax) to a CLR type.</summary>
    private Type? ResolveTypeArgExpr(Expression expr)
    {
        if (expr is IdentifierExpr ident)
        {
            return ident.Name switch
            {
                "int" => typeof(int),
                "long" => typeof(long),
                "float" => typeof(double),
                "bool" => typeof(bool),
                "str" => typeof(string),
                "byte" => typeof(byte),
                "char" => typeof(char),
                "object" => typeof(object),
                _ => null,
            } ?? ResolveIdentToClrType(ident.Name);
        }
        return null;
    }

    private Type? ResolveIdentToClrType(string name)
    {
        var symbol = _currentScope.Lookup(name);
        if (symbol?.Kind == SymbolKind.DotNetType && symbol.Type is DotNetType dnt)
            return dnt.ClrBackingType;
        return null;
    }

    private CulebralType InferMemberAccess(MemberAccessExpr member)
    {
        // self.field → resolve as field access on the current class (Python compatibility)
        if (member.Object is IdentifierExpr { Name: "self" })
        {
            var fieldSymbol = _currentScope.Lookup(member.Member);
            if (fieldSymbol is not null)
                return fieldSymbol.Type;
        }

        var objType = InferType(member.Object);

        // Named tuple member access: result.name → element type at corresponding index
        if (objType is TupleCulebralType tupleType)
        {
            for (int i = 0; i < tupleType.Names.Length; i++)
            {
                if (tupleType.Names[i] == member.Member)
                    return tupleType.Elements[i];
            }
            _diagnostics.Error("CUL2200", $"Tuple type {tupleType.DisplayName} has no member '{member.Member}'", member.Span);
            return ErrorType.Instance;
        }

        // .NET type static member access: File.read_all_text
        if (objType is DotNetType dotNetType)
        {
            var clrType = dotNetType.ClrBackingType;
            var pascalName = DotNetTypeResolver.SnakeToPascal(member.Member);

            // Try property
            var prop = _dotNetResolver.ResolveProperty(clrType, member.Member, isStatic: true);
            if (prop is not null)
                return DotNetTypeResolver.ClrTypeToCulebral(prop.PropertyType);

            // Try field
            var field = _dotNetResolver.ResolveField(clrType, member.Member, isStatic: true);
            if (field is not null)
                return DotNetTypeResolver.ClrTypeToCulebral(field.FieldType);

            // Could be a method — return type resolved at call site
            return PrimitiveType.Object;
        }

        // .NET instance member access: client.status_code
        if (objType is DotNetType { ClrBackingType: var instType })
        {
            var prop = _dotNetResolver.ResolveProperty(instType, member.Member, isStatic: false);
            if (prop is not null)
                return DotNetTypeResolver.ClrTypeToCulebral(prop.PropertyType);
            return PrimitiveType.Object;
        }

        // .NET namespace member: io.File → resolve System.IO.File
        if (objType is DotNetNamespaceType nsType)
        {
            var fullTypeName = $"{nsType.Namespace}.{member.Member}";
            var clrType = _dotNetResolver.ResolveType(fullTypeName);
            if (clrType is not null)
                return new DotNetType(fullTypeName, clrType);
        }

        return PrimitiveType.Object;
    }

    private CulebralType InferIndex(IndexExpr index)
    {
        InferType(index.Object);
        InferType(index.Index);
        // TODO: resolve element type from container type
        return PrimitiveType.Object;
    }

    private CulebralType InferList(ListExpr list)
    {
        if (list.Elements.Count == 0)
            return new GenericInstanceType("list", [PrimitiveType.Object], typeof(List<object>));

        var elementType = InferType(list.Elements[0]);
        for (int i = 1; i < list.Elements.Count; i++)
        {
            var t = InferType(list.Elements[i]);
            if (t != elementType)
                elementType = PrimitiveType.Object; // Heterogeneous list
        }

        return new GenericInstanceType("list", [elementType], typeof(List<object>));
    }

    private CulebralType InferDict(DictExpr dict)
    {
        if (dict.Entries.Count == 0)
            return new GenericInstanceType("dict", [PrimitiveType.Object, PrimitiveType.Object], typeof(Dictionary<object, object>));

        var keyType = InferType(dict.Entries[0].Key);
        var valType = InferType(dict.Entries[0].Value);
        // Infer all remaining entries so their types are recorded for boxing in lowering
        for (int i = 1; i < dict.Entries.Count; i++)
        {
            var kt = InferType(dict.Entries[i].Key);
            var vt = InferType(dict.Entries[i].Value);
            if (kt != keyType) keyType = PrimitiveType.Object;
            if (vt != valType) valType = PrimitiveType.Object;
        }

        return new GenericInstanceType("dict", [keyType, valType], typeof(Dictionary<object, object>));
    }

    private CulebralType InferSet(SetExpr set)
    {
        if (set.Elements.Count == 0)
            return new GenericInstanceType("set", [PrimitiveType.Object], typeof(HashSet<object>));

        var elementType = InferType(set.Elements[0]);
        // Infer all remaining elements so their types are recorded for boxing in lowering
        for (int i = 1; i < set.Elements.Count; i++)
        {
            var t = InferType(set.Elements[i]);
            if (t != elementType)
                elementType = PrimitiveType.Object;
        }
        return new GenericInstanceType("set", [elementType], typeof(HashSet<object>));
    }

    private CulebralType InferTuple(TupleExpr tuple)
    {
        var types = tuple.Elements.Select(InferType).ToArray();
        var names = new string?[types.Length]; // No names for expression tuples
        return new TupleCulebralType(types, names);
    }

    private CulebralType InferConditional(ConditionalExpr cond)
    {
        InferType(cond.Condition);
        var trueType = InferType(cond.TrueExpr);
        var falseType = InferType(cond.FalseExpr);

        if (trueType == falseType)
            return trueType;
        return PrimitiveType.Object; // Widened
    }

    private CulebralType InferLambda(LambdaExpr lambda)
    {
        // Create a child scope for lambda parameters
        var lambdaScope = _currentScope.CreateChild("<lambda>");

        foreach (var param in lambda.Parameters)
        {
            var paramType = param.Type is not null
                ? ResolveTypeAnnotation(param.Type)
                : PrimitiveType.Object;
            lambdaScope.TryDeclare(new Symbol
            {
                Name = param.Name,
                Kind = SymbolKind.Parameter,
                Type = paramType,
            });
        }

        var prevScope = _currentScope;
        _currentScope = lambdaScope;
        InferType(lambda.Body);
        _currentScope = prevScope;

        return PrimitiveType.Object; // Lambda returns object (delegate)
    }

    private CulebralType InferListComprehension(ListComprehension comp)
    {
        // Create a scope for all comprehension clauses (variables, conditions) and element
        var compScope = _currentScope.CreateChild("<comprehension>");
        var prevScope = _currentScope;
        _currentScope = compScope;

        foreach (var clause in comp.Clauses)
        {
            InferType(clause.Iterable);
            compScope.TryDeclare(new Symbol
            {
                Name = clause.Variable,
                Kind = SymbolKind.Variable,
                Type = PrimitiveType.Object,
            });
            if (clause.Condition is not null)
                InferType(clause.Condition);
        }

        var elemType = InferType(comp.Element);
        _currentScope = prevScope;

        return new GenericInstanceType("list", [elemType], null);
    }

    private CulebralType InferGeneratorExpr(GeneratorExpr gen)
    {
        var compScope = _currentScope.CreateChild("<generator>");
        var prevScope = _currentScope;
        _currentScope = compScope;

        foreach (var clause in gen.Clauses)
        {
            InferType(clause.Iterable);
            compScope.TryDeclare(new Symbol
            {
                Name = clause.Variable,
                Kind = SymbolKind.Variable,
                Type = PrimitiveType.Object,
            });
            if (clause.Condition is not null)
                InferType(clause.Condition);
        }

        var elemType = InferType(gen.Element);
        _currentScope = prevScope;

        // Generator expressions are eagerly evaluated as lists for now
        return new GenericInstanceType("list", [elemType], null);
    }

    // ─── Type Resolution ───

    public CulebralType ResolveTypeAnnotation(TypeAnnotation annotation)
    {
        return annotation switch
        {
            SimpleType simple => ResolveSimpleType(simple.Name),
            NullableType nullable => new NullableCulebralType(ResolveTypeAnnotation(nullable.Inner)),
            GenericType generic => ResolveGenericType(generic),
            TupleType tuple => ResolveTupleType(tuple),
            _ => ErrorType.Instance,
        };
    }

    private CulebralType ResolveSimpleType(string name)
    {
        return name switch
        {
            "int" => PrimitiveType.Int,
            "long" => PrimitiveType.Long,
            "float" => PrimitiveType.Float,
            "bool" => PrimitiveType.Bool,
            "str" => PrimitiveType.Str,
            "byte" => PrimitiveType.Byte,
            "char" => PrimitiveType.Char,
            "void" or "None" => PrimitiveType.Void,
            "object" => PrimitiveType.Object,
            _ => LookupUserType(name),
        };
    }

    private CulebralType LookupUserType(string name)
    {
        var symbol = _currentScope.Lookup(name);
        if (symbol is { Kind: SymbolKind.Type })
            return symbol.Type;

        // Check if it's a known type parameter (registered during checking, may not be in current scope during lowering)
        if (_knownTypeParams.Contains(name))
            return new TypeParameterType(name);

        _diagnostics.Error("LEB2005", $"Unknown type '{name}'", SourceSpan.None);
        return ErrorType.Instance;
    }

    private CulebralType ResolveGenericType(GenericType generic)
    {
        var typeArgs = generic.TypeArgs.Select(ResolveTypeAnnotation).ToArray();

        // Verify generic constraints for user-defined types
        if (_typeParamNames.TryGetValue(generic.Name, out var paramNames))
        {
            for (int i = 0; i < Math.Min(typeArgs.Length, paramNames.Count); i++)
            {
                var constraintKey = $"{generic.Name}.{paramNames[i]}";
                if (_typeParamConstraints.TryGetValue(constraintKey, out var constraint))
                {
                    CheckGenericConstraint(typeArgs[i], constraint, paramNames[i], generic.Name, generic.Span);
                }
            }
        }

        // Map well-known generic types to CLR types
        Type? clrType = generic.Name switch
        {
            "list" => typeof(List<>),
            "dict" => typeof(Dictionary<,>),
            "set" => typeof(HashSet<>),
            _ => null,
        };

        return new GenericInstanceType(generic.Name, typeArgs, clrType);
    }

    private CulebralType ResolveTupleType(TupleType tuple)
    {
        var types = tuple.Elements.Select(e => ResolveTypeAnnotation(e.Type)).ToArray();
        var names = tuple.Elements.Select(e => e.Name).ToArray();
        return new TupleCulebralType(types, names);
    }

    // ─── Generic Constraint Registration & Checking ───

    /// <summary>
    /// Register type parameter names and constraints for a user-defined type.
    /// </summary>
    private void RegisterTypeParameters(string typeName, List<TypeParameter>? typeParams)
    {
        if (typeParams is null || typeParams.Count == 0)
            return;

        var names = new List<string>();
        foreach (var tp in typeParams)
        {
            names.Add(tp.Name);
            if (tp.Constraint is not null)
            {
                var constraintType = ResolveTypeAnnotation(tp.Constraint);
                if (constraintType is not ErrorType)
                {
                    _typeParamConstraints[$"{typeName}.{tp.Name}"] = constraintType;
                }
            }
        }
        _typeParamNames[typeName] = names;
    }

    /// <summary>
    /// Check that a type argument satisfies a generic constraint.
    /// The constraint can be an interface (type arg must implement it)
    /// or a class (type arg must inherit from it).
    /// </summary>
    private void CheckGenericConstraint(CulebralType typeArg, CulebralType constraint,
        string paramName, string typeName, SourceSpan span)
    {
        // Skip checking type parameters (T satisfying constraints is checked at instantiation)
        if (typeArg is TypeParameterType || typeArg is ErrorType)
            return;

        // For interface constraints: the type arg must be a class that lists the interface as a base
        // For class constraints: the type arg must inherit from the constraint class
        // Since we use type erasure and don't track full inheritance chains yet,
        // we emit a diagnostic only when we can definitively detect a violation.
        if (constraint is InterfaceType ifaceConstraint)
        {
            // Primitive types don't implement user-defined interfaces
            if (typeArg is PrimitiveType)
            {
                _diagnostics.Error("LEB2020",
                    $"Type '{typeArg.DisplayName}' does not satisfy constraint '{ifaceConstraint.DisplayName}' on type parameter '{paramName}' of '{typeName}'",
                    span);
            }
        }
        else if (constraint is ClassType classConstraint)
        {
            // Primitive types don't inherit from user-defined classes
            if (typeArg is PrimitiveType)
            {
                _diagnostics.Error("LEB2020",
                    $"Type '{typeArg.DisplayName}' does not satisfy constraint '{classConstraint.DisplayName}' on type parameter '{paramName}' of '{typeName}'",
                    span);
            }
        }
    }

    // ─── Type Compatibility ───

    private bool IsAssignable(CulebralType source, CulebralType target)
    {
        if (source == target) return true;
        if (target == PrimitiveType.Object) return true;
        if (source is ErrorType || target is ErrorType) return true; // Don't cascade errors
        if (target is NullableCulebralType nullable && source == nullable.Inner) return true;
        if (source is NullableCulebralType && target is NullableCulebralType) return true;

        // Numeric widening
        if (source == PrimitiveType.Int && target == PrimitiveType.Float) return true;
        if (source == PrimitiveType.Int && target == PrimitiveType.Long) return true;

        return false;
    }
}
