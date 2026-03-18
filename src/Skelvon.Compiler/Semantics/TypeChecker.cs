using Skelvon.Compiler.Diagnostics;
using Skelvon.Compiler.Parser;

namespace Skelvon.Compiler.Semantics;

/// <summary>
/// Two-pass type checker:
///   Pass 1: Collect all top-level declarations (functions, classes, etc.)
///   Pass 2: Check function bodies, infer local types, verify constraints.
/// </summary>
public sealed class TypeChecker
{
    private readonly DiagnosticBag _diagnostics;
    private SymbolScope _currentScope;
    private readonly Dictionary<AstNode, SkelvonType> _resolvedTypes = new();
    private string? _currentClassName;
    private readonly HashSet<string> _knownTypeParams = new();

    public TypeChecker(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
        _currentScope = BuiltinSymbols.CreateGlobalScope();
    }

    public IReadOnlyDictionary<AstNode, SkelvonType> ResolvedTypes => _resolvedTypes;
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
                break;
            case RecordDef rec:
                _currentScope.TryDeclare(new Symbol
                {
                    Name = rec.Name,
                    Kind = SymbolKind.Type,
                    Type = new RecordType(rec.Name, rec.Name),
                });
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
        _currentScope = funcScope;
        CheckBlock(func.Body);
        _currentScope = prevScope;
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
                InferType(ifStmt.Condition);
                CheckBlock(ifStmt.Body);
                foreach (var elif in ifStmt.Elifs)
                {
                    InferType(elif.Condition);
                    CheckBlock(elif.Body);
                }
                if (ifStmt.ElseBody is not null)
                    CheckBlock(ifStmt.ElseBody);
                break;

            case WhileStatement whileStmt:
                InferType(whileStmt.Condition);
                CheckBlock(whileStmt.Body);
                break;

            case ForStatement forStmt:
                CheckForStatement(forStmt);
                break;

            case WithStatement withStmt:
                foreach (var item in withStmt.Items)
                    InferType(item.ContextExpr);
                CheckBlock(withStmt.Body);
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

            case ImportStatement:
            case FromImportStatement:
            case WhenStatement:
            case BreakStatement:
            case ContinueStatement:
            case PassStatement:
            case YieldStatement:
            case RaiseStatement:
                break; // Valid but no type checking needed at this stage
        }
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
                    _diagnostics.Error("SKV2001",
                        $"Cannot assign {valueType.DisplayName} to variable '{ident.Name}' of type {existing.Type.DisplayName}",
                        assign.Span);
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
                _diagnostics.Error("SKV2002",
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

        // Create a child scope for the loop variable
        var loopScope = _currentScope.CreateChild("<for>");
        loopScope.TryDeclare(new Symbol
        {
            Name = forStmt.Variable,
            Kind = SymbolKind.Variable,
            Type = PrimitiveType.Object, // TODO: infer element type from iterable
        });

        var prevScope = _currentScope;
        _currentScope = loopScope;
        CheckBlock(forStmt.Body);
        _currentScope = prevScope;
    }

    // ─── Type Inference ───

    public SkelvonType InferType(Expression expr)
    {
        var type = InferTypeCore(expr);
        _resolvedTypes[expr] = type;
        return type;
    }

    private SkelvonType InferTypeCore(Expression expr)
    {
        return expr switch
        {
            IntLiteralExpr => PrimitiveType.Int,
            FloatLiteralExpr => PrimitiveType.Float,
            StringLiteralExpr => PrimitiveType.Str,
            FStringExpr fstr => InferFString(fstr),
            BoolLiteralExpr => PrimitiveType.Bool,
            NoneLiteralExpr => new NullableSkelvonType(PrimitiveType.Object),

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

            LambdaExpr => PrimitiveType.Object, // TODO: function type inference
            ConditionalExpr cond => InferConditional(cond),
            AwaitExpr await_ => InferType(await_.Operand), // Simplified
            IsExpr => PrimitiveType.Bool,
            InExpr => PrimitiveType.Bool,

            ListComprehension comp => InferListComprehension(comp),
            DictComprehension => PrimitiveType.Object,
            GeneratorExpr => PrimitiveType.Object,

            WithExpr with_ => InferType(with_.Source),

            _ => PrimitiveType.Object,
        };
    }

    private SkelvonType InferFString(FStringExpr fstr)
    {
        foreach (var part in fstr.Parts)
        {
            if (part is FStringInterpolation interp)
                InferType(interp.Expr);
        }
        return PrimitiveType.Str;
    }

    private SkelvonType InferIdentifier(IdentifierExpr ident)
    {
        var symbol = _currentScope.Lookup(ident.Name);
        if (symbol is null)
        {
            _diagnostics.Error("SKV2003", $"Undefined name '{ident.Name}'", ident.Span);
            return ErrorType.Instance;
        }
        return symbol.Type;
    }

    private SkelvonType InferFieldAccess(FieldAccessExpr field)
    {
        // @field_name — look up in the current class scope
        var symbol = _currentScope.Lookup(field.FieldName);
        if (symbol is null)
        {
            _diagnostics.Error("SKV2004", $"Undefined field '@{field.FieldName}'", field.Span);
            return ErrorType.Instance;
        }
        return symbol.Type;
    }

    private SkelvonType InferBinary(BinaryExpr bin)
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

    private SkelvonType InferUnary(UnaryExpr unary)
    {
        var operandType = InferType(unary.Operand);
        if (unary.Op == Lexer.TokenKind.KwNot)
            return PrimitiveType.Bool;
        return operandType;
    }

    private SkelvonType InferCall(CallExpr call)
    {
        var calleeType = InferType(call.Callee);

        foreach (var arg in call.Arguments)
            InferType(arg.Value);

        if (calleeType is FunctionType funcType)
            return funcType.ReturnType;

        // Constructor call — type name used as function
        if (call.Callee is IdentifierExpr ident)
        {
            var symbol = _currentScope.Lookup(ident.Name);
            if (symbol?.Kind == SymbolKind.Type)
                return symbol.Type;
        }

        return PrimitiveType.Object;
    }

    private SkelvonType InferMemberAccess(MemberAccessExpr member)
    {
        InferType(member.Object);
        // TODO: resolve member from type's symbol table
        return PrimitiveType.Object;
    }

    private SkelvonType InferIndex(IndexExpr index)
    {
        InferType(index.Object);
        InferType(index.Index);
        // TODO: resolve element type from container type
        return PrimitiveType.Object;
    }

    private SkelvonType InferList(ListExpr list)
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

        return new GenericInstanceType("list", [elementType], null);
    }

    private SkelvonType InferDict(DictExpr dict)
    {
        if (dict.Entries.Count == 0)
            return new GenericInstanceType("dict", [PrimitiveType.Object, PrimitiveType.Object], null);

        var keyType = InferType(dict.Entries[0].Key);
        var valType = InferType(dict.Entries[0].Value);

        return new GenericInstanceType("dict", [keyType, valType], null);
    }

    private SkelvonType InferSet(SetExpr set)
    {
        if (set.Elements.Count == 0)
            return new GenericInstanceType("set", [PrimitiveType.Object], null);

        var elementType = InferType(set.Elements[0]);
        return new GenericInstanceType("set", [elementType], null);
    }

    private SkelvonType InferTuple(TupleExpr tuple)
    {
        var types = tuple.Elements.Select(InferType).ToArray();
        var names = new string?[types.Length]; // No names for expression tuples
        return new TupleSkelvonType(types, names);
    }

    private SkelvonType InferConditional(ConditionalExpr cond)
    {
        InferType(cond.Condition);
        var trueType = InferType(cond.TrueExpr);
        var falseType = InferType(cond.FalseExpr);

        if (trueType == falseType)
            return trueType;
        return PrimitiveType.Object; // Widened
    }

    private SkelvonType InferListComprehension(ListComprehension comp)
    {
        InferType(comp.Iterable);
        if (comp.Condition is not null)
            InferType(comp.Condition);

        // Create a scope for the comprehension variable
        var compScope = _currentScope.CreateChild("<comprehension>");
        compScope.TryDeclare(new Symbol
        {
            Name = comp.Variable,
            Kind = SymbolKind.Variable,
            Type = PrimitiveType.Object,
        });

        var prevScope = _currentScope;
        _currentScope = compScope;
        var elemType = InferType(comp.Element);
        _currentScope = prevScope;

        return new GenericInstanceType("list", [elemType], null);
    }

    // ─── Type Resolution ───

    public SkelvonType ResolveTypeAnnotation(TypeAnnotation annotation)
    {
        return annotation switch
        {
            SimpleType simple => ResolveSimpleType(simple.Name),
            NullableType nullable => new NullableSkelvonType(ResolveTypeAnnotation(nullable.Inner)),
            GenericType generic => ResolveGenericType(generic),
            TupleType tuple => ResolveTupleType(tuple),
            _ => ErrorType.Instance,
        };
    }

    private SkelvonType ResolveSimpleType(string name)
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

    private SkelvonType LookupUserType(string name)
    {
        var symbol = _currentScope.Lookup(name);
        if (symbol is { Kind: SymbolKind.Type })
            return symbol.Type;

        // Check if it's a known type parameter (registered during checking, may not be in current scope during lowering)
        if (_knownTypeParams.Contains(name))
            return new TypeParameterType(name);

        _diagnostics.Error("SKV2005", $"Unknown type '{name}'", SourceSpan.None);
        return ErrorType.Instance;
    }

    private SkelvonType ResolveGenericType(GenericType generic)
    {
        var typeArgs = generic.TypeArgs.Select(ResolveTypeAnnotation).ToArray();

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

    private SkelvonType ResolveTupleType(TupleType tuple)
    {
        var types = tuple.Elements.Select(e => ResolveTypeAnnotation(e.Type)).ToArray();
        var names = tuple.Elements.Select(e => e.Name).ToArray();
        return new TupleSkelvonType(types, names);
    }

    // ─── Type Compatibility ───

    private bool IsAssignable(SkelvonType source, SkelvonType target)
    {
        if (source == target) return true;
        if (target == PrimitiveType.Object) return true;
        if (source is ErrorType || target is ErrorType) return true; // Don't cascade errors
        if (target is NullableSkelvonType nullable && source == nullable.Inner) return true;
        if (source is NullableSkelvonType && target is NullableSkelvonType) return true;

        // Numeric widening
        if (source == PrimitiveType.Int && target == PrimitiveType.Float) return true;
        if (source == PrimitiveType.Int && target == PrimitiveType.Long) return true;

        return false;
    }
}
