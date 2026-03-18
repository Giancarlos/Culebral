using Skelvon.Compiler.Diagnostics;
using Skelvon.Compiler.Lexer;
using Skelvon.Compiler.Parser;
using Skelvon.Compiler.Semantics;

namespace Skelvon.Compiler.IR;

/// <summary>
/// Lowers the type-checked AST into SkelvonIR.
/// Desugars comprehensions, pattern matching, with-statements, etc.
/// Produces basic blocks with stack-based instructions.
/// </summary>
public sealed class IrLowering
{
    private readonly DiagnosticBag _diagnostics;
    private readonly TypeChecker _typeChecker;

    private IrFunction? _currentFunction;
    private IrBasicBlock? _currentBlock;
    private int _blockCounter;
    private int _localCounter;

    // Loop context for break/continue
    private readonly Stack<(string BreakLabel, string ContinueLabel)> _loopStack = new();

    // Class context — set when lowering methods inside a type
    private string? _currentDeclaringType;
    private IrTypeDef? _currentTypeDef;

    // Track known user types for constructor call detection
    private readonly HashSet<string> _knownTypes = new();

    // Store function definitions for default parameter lookup
    private readonly Dictionary<string, FunctionDef> _functionDefs = new();

    // Store lowered type definitions for property/field resolution
    private readonly Dictionary<string, IrTypeDef> _typeDefs = new();

    public IrLowering(DiagnosticBag diagnostics, TypeChecker typeChecker)
    {
        _diagnostics = diagnostics;
        _typeChecker = typeChecker;
    }

    public IrModule Lower(CompilationUnit unit, string moduleName, string sourcePath)
    {
        var module = new IrModule { Name = moduleName, SourcePath = sourcePath };

        // Collect known type and function names
        foreach (var node in unit.Statements)
        {
            if (node is ClassDef c) _knownTypes.Add(c.Name);
            else if (node is StructDef s) _knownTypes.Add(s.Name);
            else if (node is RecordDef r) _knownTypes.Add(r.Name);
            else if (node is EnumDef e) _knownTypes.Add(e.Name);
            else if (node is FunctionDef f) _functionDefs[f.Name] = f;
        }

        // First pass: lower type definitions
        foreach (var node in unit.Statements)
        {
            switch (node)
            {
                case ClassDef cls:
                {
                    var td = LowerClass(cls);
                    module.Types.Add(td);
                    _typeDefs[cls.Name] = td;
                    break;
                }
                case StructDef strct:
                {
                    var td = LowerStruct(strct);
                    module.Types.Add(td);
                    _typeDefs[strct.Name] = td;
                    break;
                }
                case RecordDef rec:
                {
                    var td = LowerRecord(rec);
                    module.Types.Add(td);
                    _typeDefs[rec.Name] = td;
                    break;
                }
                case EnumDef enumDef:
                    module.Types.AddRange(LowerEnum(enumDef));
                    break;
                case InterfaceDef iface:
                {
                    var td = LowerInterface(iface);
                    module.Types.Add(td);
                    _typeDefs[iface.Name] = td;
                    break;
                }
            }
        }

        // Second pass: lower functions
        foreach (var node in unit.Statements)
        {
            if (node is FunctionDef func)
            {
                var irFunc = LowerFunction(func);
                module.Functions.Add(irFunc);
                if (func.Name == "main" || func.Name == "__main__")
                    module.EntryPoint = irFunc;
            }
        }

        // Collect top-level statements (script mode)
        var topLevelStatements = unit.Statements
            .Where(s => s is Statement and not FunctionDef and not ClassDef and not StructDef
                        and not RecordDef and not EnumDef and not InterfaceDef
                        and not ImportStatement and not FromImportStatement and not WhenStatement)
            .Cast<Statement>()
            .ToList();

        if (topLevelStatements.Count > 0 && module.EntryPoint is null)
        {
            // Wrap top-level code in a synthetic main function
            var syntheticMain = LowerTopLevelStatements(topLevelStatements);
            module.Functions.Add(syntheticMain);
            module.EntryPoint = syntheticMain;
        }

        return module;
    }

    // ─── Type Lowering ───

    private IrTypeDef LowerClass(ClassDef cls)
    {
        // Determine base type vs interfaces
        // If a base name resolves to an interface type, it's an interface implementation, not inheritance
        string? baseTypeName = null;
        var interfaceNames = new List<string>();
        foreach (var b in cls.Bases)
        {
            if (b is SimpleType st)
            {
                var sym = _typeChecker.GlobalScope.Lookup(st.Name);
                if (sym?.Type is InterfaceType)
                    interfaceNames.Add(st.Name);
                else if (baseTypeName is null)
                    baseTypeName = st.Name;
                else
                    interfaceNames.Add(st.Name); // Additional bases treated as interfaces
            }
        }

        var typeDef = new IrTypeDef
        {
            Name = cls.Name,
            Kind = IrTypeKind.Class,
            BaseType = baseTypeName,
        };

        _currentTypeDef = typeDef;

        // Collect fields first (needed for constructor)
        foreach (var member in cls.Members)
        {
            if (member is FieldDeclaration field)
            {
                typeDef.Fields.Add(new IrField
                {
                    Name = field.Name,
                    Type = _typeChecker.ResolveTypeAnnotation(field.Type),
                    DefaultValue = field.Default is not null ? LowerFieldDefault(field.Default) : null,
                });
            }
        }

        // Lower methods, separating __init__ as constructor
        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case FunctionDef { Name: "__init__" } initMethod:
                    typeDef.Constructor = LowerConstructor(initMethod, cls.Name, typeDef);
                    break;
                case FunctionDef method:
                    typeDef.Methods.Add(LowerMethod(method, cls.Name));
                    break;
                case PropertyDef prop:
                    typeDef.Properties.Add(LowerProperty(prop, cls.Name));
                    break;
            }
        }

        // Add interfaces
        foreach (var iface in interfaceNames)
            typeDef.Interfaces.Add(iface);

        _currentTypeDef = null;
        return typeDef;
    }

    private IrInstruction? LowerFieldDefault(Expression expr)
    {
        // Return a simple constant instruction for the default value
        return expr switch
        {
            IntLiteralExpr i => new IrLoadInt(i.Value, expr.Span),
            FloatLiteralExpr f => new IrLoadFloat(f.Value, expr.Span),
            StringLiteralExpr s => new IrLoadString(s.Value, expr.Span),
            BoolLiteralExpr b => new IrLoadBool(b.Value, expr.Span),
            NoneLiteralExpr => new IrLoadNull(expr.Span),
            _ => null, // Complex defaults handled in constructor body
        };
    }

    private IrFunction LowerConstructor(FunctionDef initMethod, string declaringType, IrTypeDef typeDef)
    {
        var parameters = initMethod.Parameters.Select((p, i) => new IrParameter
        {
            Name = p.Name,
            Type = _typeChecker.ResolveTypeAnnotation(p.Type),
            Index = i + 1, // +1 because arg 0 is 'this'
        }).ToList();

        var entryBlock = new IrBasicBlock { Label = NewBlockLabel("ctor_entry") };
        var body = new List<IrBasicBlock> { entryBlock };

        var irFunc = new IrFunction
        {
            Name = ".ctor",
            ReturnType = PrimitiveType.Void,
            Parameters = parameters,
            Body = body,
            IsStatic = false,
            DeclaringType = declaringType,
        };

        _currentFunction = irFunc;
        _currentBlock = entryBlock;
        _currentDeclaringType = declaringType;
        _localCounter = 0;

        // Initialize fields with default values
        foreach (var field in typeDef.Fields)
        {
            if (field.DefaultValue is not null)
            {
                _currentBlock.Emit(new IrLoadThis(initMethod.Span));
                _currentBlock.Emit(field.DefaultValue);
                _currentBlock.Emit(new IrStoreField(declaringType, field.Name, initMethod.Span));
            }
        }

        // Lower the __init__ body
        LowerBlock(initMethod.Body);

        if (_currentBlock is not null && !EndsWithReturn(_currentBlock))
            _currentBlock.Emit(new IrReturn(false, initMethod.Span));

        _currentFunction = null;
        _currentBlock = null;
        _currentDeclaringType = null;

        return irFunc;
    }

    private IrTypeDef LowerStruct(StructDef strct)
    {
        var typeDef = new IrTypeDef { Name = strct.Name, Kind = IrTypeKind.Struct };
        _currentTypeDef = typeDef;

        // Collect fields first
        foreach (var member in strct.Members)
        {
            if (member is FieldDeclaration field)
            {
                typeDef.Fields.Add(new IrField
                {
                    Name = field.Name,
                    Type = _typeChecker.ResolveTypeAnnotation(field.Type),
                    DefaultValue = field.Default is not null ? LowerFieldDefault(field.Default) : null,
                });
            }
        }

        // Methods and constructor
        foreach (var member in strct.Members)
        {
            switch (member)
            {
                case FunctionDef { Name: "__init__" } initMethod:
                    typeDef.Constructor = LowerConstructor(initMethod, strct.Name, typeDef);
                    break;
                case FunctionDef method:
                    typeDef.Methods.Add(LowerMethod(method, strct.Name));
                    break;
            }
        }

        _currentTypeDef = null;
        return typeDef;
    }

    private IrTypeDef LowerRecord(RecordDef rec)
    {
        var typeDef = new IrTypeDef { Name = rec.Name, Kind = IrTypeKind.Record };
        _currentTypeDef = typeDef;

        foreach (var member in rec.Members)
        {
            switch (member)
            {
                case FieldDeclaration field:
                    typeDef.Fields.Add(new IrField
                    {
                        Name = field.Name,
                        Type = _typeChecker.ResolveTypeAnnotation(field.Type),
                    });
                    break;
                case FunctionDef { Name: "__init__" } initMethod:
                    typeDef.Constructor = LowerConstructor(initMethod, rec.Name, typeDef);
                    break;
                case FunctionDef method:
                    typeDef.Methods.Add(LowerMethod(method, rec.Name));
                    break;
            }
        }

        _currentTypeDef = null;
        return typeDef;
    }

    private List<IrTypeDef> LowerEnum(EnumDef enumDef)
    {
        var types = new List<IrTypeDef>();

        // Abstract base class
        var baseDef = new IrTypeDef
        {
            Name = enumDef.Name,
            Kind = IrTypeKind.AbstractClass,
        };
        types.Add(baseDef);

        // Sealed variant classes
        foreach (var variant in enumDef.Variants)
        {
            var variantDef = new IrTypeDef
            {
                Name = $"{enumDef.Name}_{variant.Name}",
                Kind = IrTypeKind.SealedClass,
                BaseType = enumDef.Name,
            };

            if (variant.Fields is not null)
            {
                foreach (var field in variant.Fields)
                {
                    variantDef.Fields.Add(new IrField
                    {
                        Name = field.Name,
                        Type = _typeChecker.ResolveTypeAnnotation(field.Type),
                    });
                }
            }

            types.Add(variantDef);
        }

        return types;
    }

    private IrTypeDef LowerInterface(InterfaceDef iface)
    {
        var typeDef = new IrTypeDef { Name = iface.Name, Kind = IrTypeKind.Interface };

        foreach (var member in iface.Members)
        {
            if (member is FunctionDef method)
            {
                typeDef.Methods.Add(LowerMethod(method, iface.Name));
            }
        }

        return typeDef;
    }

    // ─── Function Lowering ───

    private IrFunction LowerFunction(FunctionDef func)
    {
        var returnType = func.ReturnType is not null
            ? _typeChecker.ResolveTypeAnnotation(func.ReturnType)
            : PrimitiveType.Void;

        var parameters = func.Parameters.Select((p, i) => new IrParameter
        {
            Name = p.Name,
            Type = _typeChecker.ResolveTypeAnnotation(p.Type),
            Index = i,
        }).ToList();

        var entryBlock = new IrBasicBlock { Label = NewBlockLabel("entry") };
        var body = new List<IrBasicBlock> { entryBlock };

        var irFunc = new IrFunction
        {
            Name = func.Name,
            ReturnType = returnType,
            Parameters = parameters,
            Body = body,
            IsStatic = true,
            IsAsync = func.IsAsync,
            IsEntryPoint = func.Name == "main",
        };

        _currentFunction = irFunc;
        _currentBlock = entryBlock;
        _localCounter = 0;

        LowerBlock(func.Body);

        // Ensure function ends with a return
        if (_currentBlock is not null && !EndsWithReturn(_currentBlock))
        {
            _currentBlock.Emit(new IrReturn(false, func.Span));
        }

        _currentFunction = null;
        _currentBlock = null;

        return irFunc;
    }

    private IrFunction LowerMethod(FunctionDef method, string declaringType)
    {
        var returnType = method.ReturnType is not null
            ? _typeChecker.ResolveTypeAnnotation(method.ReturnType)
            : PrimitiveType.Void;

        // Instance methods: params start at index 1 (arg 0 = this)
        var parameters = method.Parameters.Select((p, i) => new IrParameter
        {
            Name = p.Name,
            Type = _typeChecker.ResolveTypeAnnotation(p.Type),
            Index = i + 1, // +1 for implicit 'this'
        }).ToList();

        var entryBlock = new IrBasicBlock { Label = NewBlockLabel("entry") };
        var body = new List<IrBasicBlock> { entryBlock };

        var irFunc = new IrFunction
        {
            Name = method.Name,
            ReturnType = returnType,
            Parameters = parameters,
            Body = body,
            IsStatic = false,
            IsAsync = method.IsAsync,
            DeclaringType = declaringType,
        };

        var prevFunction = _currentFunction;
        var prevBlock = _currentBlock;
        var prevDeclaringType = _currentDeclaringType;
        var prevLocalCounter = _localCounter;

        _currentFunction = irFunc;
        _currentBlock = entryBlock;
        _currentDeclaringType = declaringType;
        _localCounter = 0;

        LowerBlock(method.Body);

        if (_currentBlock is not null && !EndsWithReturn(_currentBlock))
            _currentBlock.Emit(new IrReturn(false, method.Span));

        _currentFunction = prevFunction;
        _currentBlock = prevBlock;
        _currentDeclaringType = prevDeclaringType;
        _localCounter = prevLocalCounter;

        return irFunc;
    }

    private IrProperty LowerProperty(PropertyDef prop, string declaringType)
    {
        var type = _typeChecker.ResolveTypeAnnotation(prop.ReturnType);

        IrFunction? getter = null;
        if (prop.Getter is not null)
        {
            var getterFunc = new FunctionDef(
                $"get_{prop.Name}", [], prop.ReturnType, prop.Getter,
                false, [], prop.Span);
            getter = LowerMethod(getterFunc, declaringType);
        }

        IrFunction? setter = null;
        if (prop.Setter is not null)
        {
            var setterFunc = new FunctionDef(
                $"set_{prop.Name}",
                [new Parameter("value", prop.ReturnType, null, false, prop.Span)],
                null, prop.Setter, false, [], prop.Span);
            setter = LowerMethod(setterFunc, declaringType);
        }

        return new IrProperty { Name = prop.Name, Type = type, Getter = getter, Setter = setter };
    }

    private IrFunction LowerTopLevelStatements(List<Statement> statements)
    {
        var entryBlock = new IrBasicBlock { Label = NewBlockLabel("entry") };
        var body = new List<IrBasicBlock> { entryBlock };

        var irFunc = new IrFunction
        {
            Name = "<Main>$",
            ReturnType = PrimitiveType.Void,
            Parameters = [],
            Body = body,
            IsStatic = true,
            IsEntryPoint = true,
        };

        _currentFunction = irFunc;
        _currentBlock = entryBlock;
        _localCounter = 0;

        foreach (var stmt in statements)
            LowerStatement(stmt);

        if (_currentBlock is not null && !EndsWithReturn(_currentBlock))
            _currentBlock.Emit(new IrReturn(false, SourceSpan.None));

        _currentFunction = null;
        _currentBlock = null;

        return irFunc;
    }

    // ─── Statement Lowering ───

    private void LowerBlock(Block block)
    {
        foreach (var stmt in block.Statements)
            LowerStatement(stmt);
    }

    private void LowerStatement(Statement stmt)
    {
        if (_currentBlock is null) return;

        switch (stmt)
        {
            case ExpressionStatement exprStmt:
                LowerExpression(exprStmt.Expr);
                // Pop the result if the expression leaves a value on the stack
                if (ExpressionLeavesValue(exprStmt.Expr))
                    _currentBlock.Emit(new IrPop(stmt.Span));
                break;

            case ReturnStatement ret:
                if (ret.Value is not null)
                {
                    LowerExpression(ret.Value);
                    _currentBlock.Emit(new IrReturn(true, ret.Span));
                }
                else
                {
                    _currentBlock.Emit(new IrReturn(false, ret.Span));
                }
                break;

            case AssignmentStatement assign:
                LowerAssignment(assign);
                break;

            case AnnotatedAssignment annotated:
                LowerAnnotatedAssignment(annotated);
                break;

            case AugmentedAssignmentStatement augAssign:
                LowerAugmentedAssignment(augAssign);
                break;

            case IfStatement ifStmt:
                LowerIf(ifStmt);
                break;

            case WhileStatement whileStmt:
                LowerWhile(whileStmt);
                break;

            case ForStatement forStmt:
                LowerFor(forStmt);
                break;

            case BreakStatement brk:
                if (_loopStack.Count > 0)
                    _currentBlock.Emit(new IrBranch(_loopStack.Peek().BreakLabel, brk.Span));
                break;

            case ContinueStatement cont:
                if (_loopStack.Count > 0)
                    _currentBlock.Emit(new IrBranch(_loopStack.Peek().ContinueLabel, cont.Span));
                break;

            case PassStatement:
                _currentBlock.Emit(new IrNop(stmt.Span));
                break;

            case RaiseStatement raise:
                if (raise.Value is not null)
                    LowerExpression(raise.Value);
                // TODO: emit throw instruction
                break;

            case FunctionDef:
            case ClassDef:
            case ImportStatement:
            case FromImportStatement:
            case WhenStatement:
                break; // Handled at module level

            default:
                _diagnostics.Warning("SKV3001", $"Unhandled statement type in lowering: {stmt.GetType().Name}", stmt.Span);
                break;
        }
    }

    private void LowerAssignment(AssignmentStatement assign)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        LowerExpression(assign.Value);

        if (assign.Target is IdentifierExpr ident)
        {
            // Check if it's an existing local or parameter first
            var existingLocal = _currentFunction.Locals.FirstOrDefault(l => l.Name == ident.Name);
            var existingParam = _currentFunction.Parameters.FirstOrDefault(p => p.Name == ident.Name);

            if (existingLocal is not null)
            {
                _currentBlock.Emit(new IrStoreLocal(existingLocal.Index, assign.Span));
            }
            else if (existingParam is not null)
            {
                // Can't store to params directly in CIL easily; use a local
                var local = GetOrCreateLocal(ident.Name, assign.Span, existingParam.Type);
                _currentBlock.Emit(new IrStoreLocal(local.Index, assign.Span));
            }
            else if (_currentDeclaringType is not null && _currentTypeDef is not null &&
                     _currentTypeDef.Fields.Any(f => f.Name == ident.Name))
            {
                // Bare name assignment to a field: count = value → this.count = value
                var fieldType = _currentTypeDef.Fields.First(f => f.Name == ident.Name).Type;
                var tempLocal = CreateLocal($"<field_tmp_{ident.Name}>", fieldType);
                _currentBlock.Emit(new IrStoreLocal(tempLocal.Index, assign.Span));
                _currentBlock.Emit(new IrLoadThis(assign.Span));
                _currentBlock.Emit(new IrLoadLocal(tempLocal.Index, assign.Span));
                _currentBlock.Emit(new IrStoreField(_currentDeclaringType, ident.Name, assign.Span));
            }
            else
            {
                // New local variable
                var valueType = _typeChecker.ResolvedTypes.TryGetValue(assign.Value, out var resolved)
                    ? resolved
                    : PrimitiveType.Object;
                var local = GetOrCreateLocal(ident.Name, assign.Span, valueType);
                _currentBlock.Emit(new IrStoreLocal(local.Index, assign.Span));
            }
        }
        else if (assign.Target is FieldAccessExpr field)
        {
            // @field = value → this.field = value
            if (_currentDeclaringType is not null && _currentTypeDef is not null)
            {
                var fType = _currentTypeDef.Fields.FirstOrDefault(f => f.Name == field.FieldName)?.Type ?? PrimitiveType.Object;
                var tempLocal = CreateLocal($"<field_tmp_{field.FieldName}>", fType);
                _currentBlock.Emit(new IrStoreLocal(tempLocal.Index, assign.Span));
                _currentBlock.Emit(new IrLoadThis(assign.Span));
                _currentBlock.Emit(new IrLoadLocal(tempLocal.Index, assign.Span));
                _currentBlock.Emit(new IrStoreField(_currentDeclaringType, field.FieldName, assign.Span));
            }
        }
        else if (assign.Target is MemberAccessExpr member)
        {
            var objType = _typeChecker.ResolvedTypes.TryGetValue(member.Object, out var ot) ? ot : null;
            var typeName = objType?.DisplayName ?? "object";

            // Check if this is a property assignment (needs setter call)
            if (_typeDefs.TryGetValue(typeName, out var memberTypeDef) &&
                memberTypeDef.Properties.Any(p => p.Name == member.Member))
            {
                // value on stack → save, load obj, load value, call setter
                var propType = memberTypeDef.Properties.First(p => p.Name == member.Member).Type;
                var tempLocal = CreateLocal("<prop_tmp>", propType);
                _currentBlock.Emit(new IrStoreLocal(tempLocal.Index, assign.Span));
                LowerExpression(member.Object);
                _currentBlock.Emit(new IrLoadLocal(tempLocal.Index, assign.Span));
                _currentBlock.Emit(new IrCallMethod(typeName, $"set_{member.Member}", 1, assign.Span));
            }
            else
            {
                // obj.field = value → value already on stack, need: obj, value, stfld
                var tempLocal = CreateLocal("<member_tmp>", PrimitiveType.Object);
                _currentBlock.Emit(new IrStoreLocal(tempLocal.Index, assign.Span));
                LowerExpression(member.Object);
                _currentBlock.Emit(new IrLoadLocal(tempLocal.Index, assign.Span));
                _currentBlock.Emit(new IrStoreField(typeName, member.Member, assign.Span));
            }
        }
    }

    private void LowerAnnotatedAssignment(AnnotatedAssignment annotated)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        var local = GetOrCreateLocal(annotated.Name, annotated.Span,
            _typeChecker.ResolveTypeAnnotation(annotated.TypeAnnotation));

        if (annotated.Value is not null)
        {
            LowerExpression(annotated.Value);
            _currentBlock.Emit(new IrStoreLocal(local.Index, annotated.Span));
        }
    }

    private void LowerAugmentedAssignment(AugmentedAssignmentStatement augAssign)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        if (augAssign.Target is IdentifierExpr ident)
        {
            // Check if it's a local
            var local = _currentFunction.Locals.FirstOrDefault(l => l.Name == ident.Name);
            if (local is not null)
            {
                _currentBlock.Emit(new IrLoadLocal(local.Index, augAssign.Span));
                LowerExpression(augAssign.Value);
                _currentBlock.Emit(new IrBinaryOp(MapAugmentedOp(augAssign.Op), local.Type, augAssign.Span));
                _currentBlock.Emit(new IrStoreLocal(local.Index, augAssign.Span));
                return;
            }

            // Check if it's a field (in instance method)
            if (_currentDeclaringType is not null && _currentTypeDef is not null &&
                _currentTypeDef.Fields.Any(f => f.Name == ident.Name))
            {
                var field = _currentTypeDef.Fields.First(f => f.Name == ident.Name);
                // Load current value: this.field
                _currentBlock.Emit(new IrLoadThis(augAssign.Span));
                _currentBlock.Emit(new IrLoadField(_currentDeclaringType, ident.Name, augAssign.Span));
                // Compute new value
                LowerExpression(augAssign.Value);
                _currentBlock.Emit(new IrBinaryOp(MapAugmentedOp(augAssign.Op), field.Type, augAssign.Span));
                // Store: this.field = result
                var tempLocal = CreateLocal("<aug_tmp>", field.Type);
                _currentBlock.Emit(new IrStoreLocal(tempLocal.Index, augAssign.Span));
                _currentBlock.Emit(new IrLoadThis(augAssign.Span));
                _currentBlock.Emit(new IrLoadLocal(tempLocal.Index, augAssign.Span));
                _currentBlock.Emit(new IrStoreField(_currentDeclaringType, ident.Name, augAssign.Span));
                return;
            }

            // Fallback: create a new local
            var newLocal = GetOrCreateLocal(ident.Name, augAssign.Span);
            _currentBlock.Emit(new IrLoadLocal(newLocal.Index, augAssign.Span));
            LowerExpression(augAssign.Value);
            _currentBlock.Emit(new IrBinaryOp(MapAugmentedOp(augAssign.Op), newLocal.Type, augAssign.Span));
            _currentBlock.Emit(new IrStoreLocal(newLocal.Index, augAssign.Span));
        }
    }

    private void LowerIf(IfStatement ifStmt)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        var thenLabel = NewBlockLabel("if_then");
        var elseLabel = NewBlockLabel("if_else");
        var endLabel = NewBlockLabel("if_end");

        LowerExpression(ifStmt.Condition);
        var firstFalseLabel = ifStmt.Elifs.Count > 0
            ? NewBlockLabel("elif_0")
            : (ifStmt.ElseBody is not null ? elseLabel : endLabel);
        _currentBlock.Emit(new IrBranchIf(thenLabel, firstFalseLabel, ifStmt.Span));

        // Then block
        var thenBlock = new IrBasicBlock { Label = thenLabel };
        _currentFunction.Body.Add(thenBlock);
        _currentBlock = thenBlock;
        LowerBlock(ifStmt.Body);
        _currentBlock?.Emit(new IrBranch(endLabel, ifStmt.Span));

        // Elif blocks
        for (int i = 0; i < ifStmt.Elifs.Count; i++)
        {
            var elif = ifStmt.Elifs[i];
            var elifLabel = i == 0 ? firstFalseLabel : $"elif_{i}";
            var elifBlock = new IrBasicBlock { Label = elifLabel };
            _currentFunction.Body.Add(elifBlock);
            _currentBlock = elifBlock;

            LowerExpression(elif.Condition);
            var elifThen = NewBlockLabel($"elif_{i}_then");
            var nextLabel = i + 1 < ifStmt.Elifs.Count
                ? NewBlockLabel($"elif_{i + 1}")
                : (ifStmt.ElseBody is not null ? elseLabel : endLabel);
            _currentBlock.Emit(new IrBranchIf(elifThen, nextLabel, elif.Span));

            var elifThenBlock = new IrBasicBlock { Label = elifThen };
            _currentFunction.Body.Add(elifThenBlock);
            _currentBlock = elifThenBlock;
            LowerBlock(elif.Body);
            _currentBlock?.Emit(new IrBranch(endLabel, elif.Span));
        }

        // Else block
        if (ifStmt.ElseBody is not null)
        {
            var elseBlock = new IrBasicBlock { Label = elseLabel };
            _currentFunction.Body.Add(elseBlock);
            _currentBlock = elseBlock;
            LowerBlock(ifStmt.ElseBody);
            _currentBlock?.Emit(new IrBranch(endLabel, ifStmt.Span));
        }

        // End block
        var end = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(end);
        _currentBlock = end;
    }

    private void LowerWhile(WhileStatement whileStmt)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        var condLabel = NewBlockLabel("while_cond");
        var bodyLabel = NewBlockLabel("while_body");
        var endLabel = NewBlockLabel("while_end");

        _currentBlock.Emit(new IrBranch(condLabel, whileStmt.Span));

        var condBlock = new IrBasicBlock { Label = condLabel };
        _currentFunction.Body.Add(condBlock);
        _currentBlock = condBlock;
        LowerExpression(whileStmt.Condition);
        _currentBlock.Emit(new IrBranchIf(bodyLabel, endLabel, whileStmt.Span));

        var bodyBlock = new IrBasicBlock { Label = bodyLabel };
        _currentFunction.Body.Add(bodyBlock);
        _currentBlock = bodyBlock;

        _loopStack.Push((endLabel, condLabel));
        LowerBlock(whileStmt.Body);
        _loopStack.Pop();

        _currentBlock?.Emit(new IrBranch(condLabel, whileStmt.Span));

        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(endBlock);
        _currentBlock = endBlock;
    }

    private void LowerFor(ForStatement forStmt)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Desugar: for x in iterable → get enumerator, while MoveNext, x = Current
        // For now, simplified to a call-based pattern
        var condLabel = NewBlockLabel("for_cond");
        var bodyLabel = NewBlockLabel("for_body");
        var endLabel = NewBlockLabel("for_end");

        // Emit iterable and get enumerator
        LowerExpression(forStmt.Iterable);
        var enumeratorLocal = CreateLocal("<enumerator>", PrimitiveType.Object);
        _currentBlock.Emit(new IrCallVirtual("GetEnumerator", 0, forStmt.Span));
        _currentBlock.Emit(new IrStoreLocal(enumeratorLocal.Index, forStmt.Span));

        _currentBlock.Emit(new IrBranch(condLabel, forStmt.Span));

        // Condition: MoveNext()
        var condBlock = new IrBasicBlock { Label = condLabel };
        _currentFunction.Body.Add(condBlock);
        _currentBlock = condBlock;
        _currentBlock.Emit(new IrLoadLocal(enumeratorLocal.Index, forStmt.Span));
        _currentBlock.Emit(new IrCallVirtual("MoveNext", 0, forStmt.Span));
        _currentBlock.Emit(new IrBranchIf(bodyLabel, endLabel, forStmt.Span));

        // Body: x = Current
        var bodyBlock = new IrBasicBlock { Label = bodyLabel };
        _currentFunction.Body.Add(bodyBlock);
        _currentBlock = bodyBlock;

        // Determine element type from iterable (range → int, otherwise object)
        var iterableType = _typeChecker.ResolvedTypes.TryGetValue(forStmt.Iterable, out var it) ? it : null;
        var elementType = PrimitiveType.Object;
        // range() returns IEnumerable<int>
        if (forStmt.Iterable is CallExpr { Callee: IdentifierExpr { Name: "range" } })
            elementType = PrimitiveType.Int;

        var loopVar = GetOrCreateLocal(forStmt.Variable, forStmt.Span, elementType);
        _currentBlock.Emit(new IrLoadLocal(enumeratorLocal.Index, forStmt.Span));
        _currentBlock.Emit(new IrCallVirtual("get_Current", 0, forStmt.Span));
        // Unbox value types returned from IEnumerator.Current (which returns object)
        if (elementType != PrimitiveType.Object)
            _currentBlock.Emit(new IrUnbox(elementType, forStmt.Span));
        _currentBlock.Emit(new IrStoreLocal(loopVar.Index, forStmt.Span));

        _loopStack.Push((endLabel, condLabel));
        LowerBlock(forStmt.Body);
        _loopStack.Pop();

        _currentBlock?.Emit(new IrBranch(condLabel, forStmt.Span));

        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(endBlock);
        _currentBlock = endBlock;
    }

    // ─── Expression Lowering ───

    private void LowerExpression(Expression expr)
    {
        if (_currentBlock is null) return;

        switch (expr)
        {
            case IntLiteralExpr intLit:
                _currentBlock.Emit(new IrLoadInt(intLit.Value, expr.Span));
                break;

            case FloatLiteralExpr floatLit:
                _currentBlock.Emit(new IrLoadFloat(floatLit.Value, expr.Span));
                break;

            case StringLiteralExpr strLit:
                _currentBlock.Emit(new IrLoadString(strLit.Value, expr.Span));
                break;

            case BoolLiteralExpr boolLit:
                _currentBlock.Emit(new IrLoadBool(boolLit.Value, expr.Span));
                break;

            case NoneLiteralExpr:
                _currentBlock.Emit(new IrLoadNull(expr.Span));
                break;

            case IdentifierExpr ident:
                LowerIdentifier(ident);
                break;

            case FieldAccessExpr field:
                // @field_name → this.field_name
                if (_currentDeclaringType is not null)
                {
                    _currentBlock.Emit(new IrLoadThis(expr.Span));
                    _currentBlock.Emit(new IrLoadField(_currentDeclaringType, field.FieldName, expr.Span));
                }
                break;

            case BinaryExpr bin:
            {
                LowerExpression(bin.Left);
                LowerExpression(bin.Right);
                // Resolve operand type for correct emission (e.g., string concat vs int add)
                SkelvonType? leftType = _typeChecker.ResolvedTypes.TryGetValue(bin.Left, out var lt) ? lt : null;
                _currentBlock.Emit(new IrBinaryOp(MapBinaryOp(bin.Op), leftType, expr.Span));
                break;
            }

            case UnaryExpr unary:
                LowerExpression(unary.Operand);
                _currentBlock.Emit(new IrUnaryOp(MapUnaryOp(unary.Op), expr.Span));
                break;

            case CallExpr call:
                LowerCall(call);
                break;

            case MemberAccessExpr member:
            {
                LowerExpression(member.Object);
                var objType = _typeChecker.ResolvedTypes.TryGetValue(member.Object, out var ot) ? ot : null;
                var typeName = objType?.DisplayName ?? "object";

                // Check if this is a property access (needs getter call instead of field load)
                if (_typeDefs.TryGetValue(typeName, out var memberTypeDef) &&
                    memberTypeDef.Properties.Any(p => p.Name == member.Member))
                {
                    _currentBlock.Emit(new IrCallMethod(typeName, $"get_{member.Member}", 0, expr.Span));
                }
                else
                {
                    _currentBlock.Emit(new IrLoadField(typeName, member.Member, expr.Span));
                }
                break;
            }

            case IndexExpr index:
                LowerExpression(index.Object);
                LowerExpression(index.Index);
                _currentBlock.Emit(new IrLoadElement(expr.Span));
                break;

            case ListExpr list:
                LowerListExpr(list);
                break;

            case TupleExpr tuple:
                foreach (var elem in tuple.Elements)
                    LowerExpression(elem);
                break;

            case FStringExpr fstr:
                LowerFString(fstr);
                break;

            case ConditionalExpr cond:
                LowerConditional(cond);
                break;

            case LambdaExpr:
                // TODO: lower to delegate
                _currentBlock.Emit(new IrLoadNull(expr.Span));
                break;

            case AwaitExpr awaitExpr:
                LowerExpression(awaitExpr.Operand);
                // TODO: emit await pattern
                break;

            case ListComprehension comp:
                LowerListComprehension(comp);
                break;

            default:
                _currentBlock.Emit(new IrLoadNull(expr.Span));
                break;
        }
    }

    private void LowerIdentifier(IdentifierExpr ident)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Check if it's a parameter
        var param = _currentFunction.Parameters.FirstOrDefault(p => p.Name == ident.Name);
        if (param is not null)
        {
            _currentBlock.Emit(new IrLoadArg(param.Index, ident.Span));
            return;
        }

        // Check if it's a local
        var local = _currentFunction.Locals.FirstOrDefault(l => l.Name == ident.Name);
        if (local is not null)
        {
            _currentBlock.Emit(new IrLoadLocal(local.Index, ident.Span));
            return;
        }

        // In an instance method, bare names can refer to fields
        if (_currentDeclaringType is not null && _currentTypeDef is not null)
        {
            var field = _currentTypeDef.Fields.FirstOrDefault(f => f.Name == ident.Name);
            if (field is not null)
            {
                _currentBlock.Emit(new IrLoadThis(ident.Span));
                _currentBlock.Emit(new IrLoadField(_currentDeclaringType, ident.Name, ident.Span));
                return;
            }
        }

        // Could be a function name or type — emit as a reference
        _currentBlock.Emit(new IrLoadNull(ident.Span)); // Placeholder
    }

    private void LowerCall(CallExpr call)
    {
        if (_currentBlock is null) return;

        // Handle built-in functions and constructor calls
        if (call.Callee is IdentifierExpr ident)
        {
            var builtins = new HashSet<string>
            {
                "print", "len", "range", "int", "float", "str", "bool",
                "sorted", "abs", "min", "max", "type", "isinstance",
                "enumerate", "zip", "map", "filter", "open",
            };

            if (builtins.Contains(ident.Name))
            {
                foreach (var arg in call.Arguments)
                    LowerExpression(arg.Value);
                _currentBlock.Emit(new IrCallBuiltin(ident.Name, call.Arguments.Count, call.Span));
                return;
            }

            // Constructor call: TypeName(args) → newobj .ctor
            if (_knownTypes.Contains(ident.Name))
            {
                foreach (var arg in call.Arguments)
                    LowerExpression(arg.Value);
                _currentBlock.Emit(new IrNewObj(ident.Name, call.Arguments.Count, call.Span));
                return;
            }

            // Regular function call — fill in default args if needed
            foreach (var arg in call.Arguments)
                LowerExpression(arg.Value);
            EmitDefaultArgs(ident.Name, call.Arguments.Count, call.Span);
            var totalArgs = GetTotalArgCount(ident.Name, call.Arguments.Count);
            _currentBlock.Emit(new IrCall(ident.Name, totalArgs, true, call.Span));
            return;
        }

        // Method call: obj.method(args)
        if (call.Callee is MemberAccessExpr member)
        {
            LowerExpression(member.Object);
            foreach (var arg in call.Arguments)
                LowerExpression(arg.Value);

            // Determine if this is a call on a known user type
            var objType = _typeChecker.ResolvedTypes.TryGetValue(member.Object, out var ot) ? ot : null;
            var typeName = objType?.DisplayName;
            if (typeName is not null && _knownTypes.Contains(typeName))
            {
                _currentBlock.Emit(new IrCallMethod(typeName, member.Member, call.Arguments.Count, call.Span));
            }
            else
            {
                _currentBlock.Emit(new IrCallVirtual(member.Member, call.Arguments.Count, call.Span));
            }
            return;
        }

        // Fallback: evaluate callee and call
        LowerExpression(call.Callee);
        foreach (var arg in call.Arguments)
            LowerExpression(arg.Value);
        _currentBlock.Emit(new IrCall("<indirect>", call.Arguments.Count, false, call.Span));
    }

    private void LowerListExpr(ListExpr list)
    {
        if (_currentBlock is null) return;

        // Create a new List<object> and add elements
        _currentBlock.Emit(new IrNewObj("System.Collections.Generic.List`1", 0, list.Span));

        foreach (var elem in list.Elements)
        {
            _currentBlock.Emit(new IrDup(list.Span));
            LowerExpression(elem);
            _currentBlock.Emit(new IrCallVirtual("Add", 1, list.Span));
        }
    }

    private void LowerFString(FStringExpr fstr)
    {
        if (_currentBlock is null) return;

        foreach (var part in fstr.Parts)
        {
            switch (part)
            {
                case FStringText text:
                    _currentBlock.Emit(new IrLoadString(text.Text, fstr.Span));
                    break;
                case FStringInterpolation interp:
                    LowerExpression(interp.Expr);
                    var exprType = _typeChecker.ResolvedTypes.TryGetValue(interp.Expr, out var et)
                        ? et : PrimitiveType.Object;
                    _currentBlock.Emit(new IrToString(exprType, fstr.Span));
                    break;
            }
        }

        _currentBlock.Emit(new IrStringConcat(fstr.Parts.Count, fstr.Span));
    }

    private void LowerConditional(ConditionalExpr cond)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        var trueLabel = NewBlockLabel("cond_true");
        var falseLabel = NewBlockLabel("cond_false");
        var endLabel = NewBlockLabel("cond_end");

        LowerExpression(cond.Condition);
        _currentBlock.Emit(new IrBranchIf(trueLabel, falseLabel, cond.Span));

        var trueBlock = new IrBasicBlock { Label = trueLabel };
        _currentFunction.Body.Add(trueBlock);
        _currentBlock = trueBlock;
        LowerExpression(cond.TrueExpr);
        _currentBlock.Emit(new IrBranch(endLabel, cond.Span));

        var falseBlock = new IrBasicBlock { Label = falseLabel };
        _currentFunction.Body.Add(falseBlock);
        _currentBlock = falseBlock;
        LowerExpression(cond.FalseExpr);
        _currentBlock.Emit(new IrBranch(endLabel, cond.Span));

        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(endBlock);
        _currentBlock = endBlock;
    }

    private void LowerListComprehension(ListComprehension comp)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Desugar: [expr for x in iterable if cond]
        // → list = new List(); for x in iterable: if cond: list.Add(expr)
        var listLocal = CreateLocal("<comp_list>", PrimitiveType.Object);
        _currentBlock.Emit(new IrNewObj("System.Collections.Generic.List`1", 0, comp.Span));
        _currentBlock.Emit(new IrStoreLocal(listLocal.Index, comp.Span));

        // Emit iteration (simplified)
        LowerExpression(comp.Iterable);
        var enumeratorLocal = CreateLocal("<comp_enum>", PrimitiveType.Object);
        _currentBlock.Emit(new IrCallVirtual("GetEnumerator", 0, comp.Span));
        _currentBlock.Emit(new IrStoreLocal(enumeratorLocal.Index, comp.Span));

        var condLabel = NewBlockLabel("comp_cond");
        var bodyLabel = NewBlockLabel("comp_body");
        var endLabel = NewBlockLabel("comp_end");

        _currentBlock.Emit(new IrBranch(condLabel, comp.Span));

        var condBlock = new IrBasicBlock { Label = condLabel };
        _currentFunction.Body.Add(condBlock);
        _currentBlock = condBlock;
        _currentBlock.Emit(new IrLoadLocal(enumeratorLocal.Index, comp.Span));
        _currentBlock.Emit(new IrCallVirtual("MoveNext", 0, comp.Span));
        _currentBlock.Emit(new IrBranchIf(bodyLabel, endLabel, comp.Span));

        var bodyBlock = new IrBasicBlock { Label = bodyLabel };
        _currentFunction.Body.Add(bodyBlock);
        _currentBlock = bodyBlock;

        var varLocal = GetOrCreateLocal(comp.Variable, comp.Span);
        _currentBlock.Emit(new IrLoadLocal(enumeratorLocal.Index, comp.Span));
        _currentBlock.Emit(new IrCallVirtual("get_Current", 0, comp.Span));
        _currentBlock.Emit(new IrStoreLocal(varLocal.Index, comp.Span));

        // Condition filter
        if (comp.Condition is not null)
        {
            var addLabel = NewBlockLabel("comp_add");
            LowerExpression(comp.Condition);
            _currentBlock.Emit(new IrBranchIf(addLabel, condLabel, comp.Span));
            var addBlock = new IrBasicBlock { Label = addLabel };
            _currentFunction.Body.Add(addBlock);
            _currentBlock = addBlock;
        }

        // Add element
        _currentBlock.Emit(new IrLoadLocal(listLocal.Index, comp.Span));
        LowerExpression(comp.Element);
        _currentBlock.Emit(new IrCallVirtual("Add", 1, comp.Span));
        _currentBlock.Emit(new IrBranch(condLabel, comp.Span));

        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(endBlock);
        _currentBlock = endBlock;

        _currentBlock.Emit(new IrLoadLocal(listLocal.Index, comp.Span));
    }

    // ─── Helpers ───

    private IrLocal GetOrCreateLocal(string name, SourceSpan span, SkelvonType? type = null)
    {
        if (_currentFunction is null)
            throw new InvalidOperationException("No current function");

        var existing = _currentFunction.Locals.FirstOrDefault(l => l.Name == name);
        if (existing is not null)
            return existing;

        return CreateLocal(name, type ?? PrimitiveType.Object);
    }

    private IrLocal CreateLocal(string name, SkelvonType type)
    {
        var local = new IrLocal
        {
            Name = name,
            Type = type,
            Index = _localCounter++,
        };
        _currentFunction!.Locals.Add(local);
        return local;
    }

    private string NewBlockLabel(string prefix) => $"{prefix}_{_blockCounter++}";

    private static bool EndsWithReturn(IrBasicBlock block)
    {
        if (block.Instructions.Count == 0) return false;
        return block.Instructions[^1] is IrReturn;
    }

    private static bool ExpressionLeavesValue(Expression expr)
    {
        // Call expressions that return void don't leave a value
        // For now, assume most expressions leave a value except calls to known void functions
        return expr is not CallExpr; // Simplified — calls handled by emitter
    }

    private static IrBinaryOpKind MapBinaryOp(TokenKind op) => op switch
    {
        TokenKind.Plus => IrBinaryOpKind.Add,
        TokenKind.Minus => IrBinaryOpKind.Sub,
        TokenKind.Star => IrBinaryOpKind.Mul,
        TokenKind.Slash => IrBinaryOpKind.Div,
        TokenKind.DoubleSlash => IrBinaryOpKind.IntDiv,
        TokenKind.Percent => IrBinaryOpKind.Mod,
        TokenKind.DoubleStar => IrBinaryOpKind.Pow,
        TokenKind.Ampersand => IrBinaryOpKind.BitAnd,
        TokenKind.Pipe => IrBinaryOpKind.BitOr,
        TokenKind.Caret => IrBinaryOpKind.BitXor,
        TokenKind.ShiftLeft => IrBinaryOpKind.ShiftLeft,
        TokenKind.ShiftRight => IrBinaryOpKind.ShiftRight,
        TokenKind.Equal => IrBinaryOpKind.Equal,
        TokenKind.NotEqual => IrBinaryOpKind.NotEqual,
        TokenKind.LessThan => IrBinaryOpKind.LessThan,
        TokenKind.GreaterThan => IrBinaryOpKind.GreaterThan,
        TokenKind.LessEqual => IrBinaryOpKind.LessEqual,
        TokenKind.GreaterEqual => IrBinaryOpKind.GreaterEqual,
        TokenKind.KwAnd => IrBinaryOpKind.LogicalAnd,
        TokenKind.KwOr => IrBinaryOpKind.LogicalOr,
        _ => IrBinaryOpKind.Add,
    };

    private static IrBinaryOpKind MapAugmentedOp(TokenKind op) => op switch
    {
        TokenKind.PlusAssign => IrBinaryOpKind.Add,
        TokenKind.MinusAssign => IrBinaryOpKind.Sub,
        TokenKind.StarAssign => IrBinaryOpKind.Mul,
        TokenKind.SlashAssign => IrBinaryOpKind.Div,
        TokenKind.DoubleSlashAssign => IrBinaryOpKind.IntDiv,
        TokenKind.PercentAssign => IrBinaryOpKind.Mod,
        TokenKind.DoubleStarAssign => IrBinaryOpKind.Pow,
        TokenKind.AmpersandAssign => IrBinaryOpKind.BitAnd,
        TokenKind.PipeAssign => IrBinaryOpKind.BitOr,
        TokenKind.CaretAssign => IrBinaryOpKind.BitXor,
        TokenKind.ShiftLeftAssign => IrBinaryOpKind.ShiftLeft,
        TokenKind.ShiftRightAssign => IrBinaryOpKind.ShiftRight,
        _ => IrBinaryOpKind.Add,
    };

    private static IrUnaryOpKind MapUnaryOp(TokenKind op) => op switch
    {
        TokenKind.Minus => IrUnaryOpKind.Negate,
        TokenKind.Tilde => IrUnaryOpKind.BitNot,
        TokenKind.KwNot => IrUnaryOpKind.LogicalNot,
        _ => IrUnaryOpKind.Negate,
    };

    /// <summary>Emit default argument values for missing positional args.</summary>
    private void EmitDefaultArgs(string funcName, int providedArgs, SourceSpan span)
    {
        if (_currentBlock is null) return;
        if (!_functionDefs.TryGetValue(funcName, out var funcDef)) return;

        for (int i = providedArgs; i < funcDef.Parameters.Count; i++)
        {
            var param = funcDef.Parameters[i];
            if (param.Default is not null)
            {
                LowerExpression(param.Default);
            }
            else
            {
                // No default — emit a zero/null value
                _currentBlock.Emit(new IrLoadNull(span));
            }
        }
    }

    private int GetTotalArgCount(string funcName, int providedArgs)
    {
        if (_functionDefs.TryGetValue(funcName, out var funcDef))
            return funcDef.Parameters.Count;
        return providedArgs;
    }
}
