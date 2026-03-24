using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Lexer;
using Culebral.Compiler.Parser;
using Culebral.Compiler.Semantics;

namespace Culebral.Compiler.IR;

/// <summary>
/// Lowers the type-checked AST into CulebralIR.
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
    private int _awaitCounter;

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

    // .NET interop: imported types resolved via reflection
    private readonly Dictionary<string, Type> _importedDotNetTypes = new();
    private readonly Dictionary<string, string> _namespaceAliases = new(); // alias → namespace

    // Extension method sources: types that have been imported and contain extension methods
    private readonly List<Type> _extensionMethodSources = new();

    // Track namespaces already scanned for extension methods to avoid duplicate work
    private readonly HashSet<string> _scannedExtensionNamespaces = new();

    // Module reference for adding generated lambda methods
    private IrModule? _module;
    private int _lambdaCounter;

    public IrLowering(DiagnosticBag diagnostics, TypeChecker typeChecker)
    {
        _diagnostics = diagnostics;
        _typeChecker = typeChecker;
    }

    public IrModule Lower(CompilationUnit unit, string moduleName, string sourcePath)
    {
        var module = new IrModule { Name = moduleName, SourcePath = sourcePath };
        _module = module;

        // Register built-in Result/Ok/Err types
        _knownTypes.Add("Result");
        _knownTypes.Add("Ok");
        _knownTypes.Add("Err");

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

        // Inject built-in Result/Ok/Err types if referenced
        InjectResultTypes(module);

        // Inject default interface method implementations into classes that don't override them
        InjectDefaultInterfaceMethods(module);

        // Process imports (populate .NET type mappings)
        foreach (var node in unit.Statements)
        {
            if (node is FromImportStatement fromImport) ProcessFromImport(fromImport);
            else if (node is ImportStatement import) ProcessImport(import);
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
                        and not ImportStatement and not FromImportStatement and not WhenStatement
                        and not TypeAliasStatement)
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

        // Extract and attach class decorators for attribute emission
        if (cls.Decorators.Count > 0)
            typeDef.Decorators = ExtractDecorators(cls.Decorators);

        // Carry generic type parameters with constraints
        if (cls.TypeParameters is not null)
        {
            foreach (var tp in cls.TypeParameters)
            {
                typeDef.TypeParameters.Add(new IrTypeParameter
                {
                    Name = tp.Name,
                    ConstraintTypeName = tp.Constraint is SimpleType st2 ? st2.Name : null,
                });
            }
        }

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

        if (strct.TypeParameters is not null)
        {
            foreach (var tp in strct.TypeParameters)
            {
                typeDef.TypeParameters.Add(new IrTypeParameter
                {
                    Name = tp.Name,
                    ConstraintTypeName = tp.Constraint is Parser.SimpleType st ? st.Name : null,
                });
            }
        }

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

        if (rec.TypeParameters is not null)
        {
            foreach (var tp in rec.TypeParameters)
            {
                typeDef.TypeParameters.Add(new IrTypeParameter
                {
                    Name = tp.Name,
                    ConstraintTypeName = tp.Constraint is Parser.SimpleType st ? st.Name : null,
                });
            }
        }

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

        if (iface.TypeParameters is not null)
        {
            foreach (var tp in iface.TypeParameters)
            {
                typeDef.TypeParameters.Add(new IrTypeParameter
                {
                    Name = tp.Name,
                    ConstraintTypeName = tp.Constraint is Parser.SimpleType st ? st.Name : null,
                });
            }
        }

        foreach (var member in iface.Members)
        {
            if (member is FunctionDef method)
            {
                typeDef.Methods.Add(LowerMethod(method, iface.Name));
            }
        }

        return typeDef;
    }

    /// <summary>
    /// Injects the built-in Result/Ok/Err type hierarchy into the module.
    /// Result is an abstract base class; Ok and Err are sealed subclasses.
    /// Each stores a value (object) and exposes is_ok, is_err, and value properties.
    /// </summary>
    private void InjectResultTypes(IrModule module)
    {
        // Don't inject if user already defined these types
        if (_typeDefs.ContainsKey("Result")) return;

        var dummySpan = SourceSpan.None;

        // --- Abstract base: Result ---
        var resultDef = new IrTypeDef
        {
            Name = "Result",
            Kind = IrTypeKind.AbstractClass,
        };
        resultDef.Fields.Add(new IrField { Name = "value", Type = PrimitiveType.Object });

        // Virtual properties on the base class — overridden by Ok and Err.
        // These dummy bodies return default values; they're dispatched via callvirt at runtime.
        var resultIsOkGetter = CreateBoolGetterBody(false, dummySpan);
        resultDef.Properties.Add(new IrProperty
        {
            Name = "is_ok",
            Type = PrimitiveType.Bool,
            Getter = new IrFunction
            {
                Name = "get_is_ok", ReturnType = PrimitiveType.Bool,
                Parameters = [], Body = [resultIsOkGetter], IsStatic = false, DeclaringType = "Result",
            },
        });
        var resultIsErrGetter = CreateBoolGetterBody(false, dummySpan);
        resultDef.Properties.Add(new IrProperty
        {
            Name = "is_err",
            Type = PrimitiveType.Bool,
            Getter = new IrFunction
            {
                Name = "get_is_err", ReturnType = PrimitiveType.Bool,
                Parameters = [], Body = [resultIsErrGetter], IsStatic = false, DeclaringType = "Result",
            },
        });
        var resultValueGetter = CreateFieldGetterBody("Result", "value", dummySpan);
        resultDef.Properties.Add(new IrProperty
        {
            Name = "value",
            Type = PrimitiveType.Object,
            Getter = new IrFunction
            {
                Name = "get_value", ReturnType = PrimitiveType.Object,
                Parameters = [], Body = [resultValueGetter], IsStatic = false, DeclaringType = "Result",
            },
        });

        module.Types.Insert(0, resultDef);
        _typeDefs["Result"] = resultDef;

        // --- Sealed: Ok : Result ---
        var okDef = new IrTypeDef
        {
            Name = "Ok",
            Kind = IrTypeKind.SealedClass,
            BaseType = "Result",
        };
        okDef.Fields.Add(new IrField { Name = "value", Type = PrimitiveType.Object });

        // Constructor: Ok(value)
        var okCtorBody = new IrBasicBlock { Label = "entry" };
        okCtorBody.Emit(new IrLoadThis(dummySpan));       // this
        okCtorBody.Emit(new IrLoadArg(1, dummySpan));     // value param (arg 0 = this, arg 1 = first param)
        okCtorBody.Emit(new IrStoreField("Ok", "value", dummySpan));
        okCtorBody.Emit(new IrReturn(false, dummySpan));
        okDef.Constructor = new IrFunction
        {
            Name = ".ctor",
            ReturnType = PrimitiveType.Void,
            Parameters = [new IrParameter { Name = "value", Type = PrimitiveType.Object, Index = 1 }],
            Body = [okCtorBody],
            IsStatic = false,
            DeclaringType = "Ok",
        };

        // Properties: is_ok (true), is_err (false), value (field)
        var okIsOkGetter = CreateBoolGetterBody(true, dummySpan);
        okDef.Properties.Add(new IrProperty
        {
            Name = "is_ok",
            Type = PrimitiveType.Bool,
            Getter = new IrFunction
            {
                Name = "get_is_ok", ReturnType = PrimitiveType.Bool,
                Parameters = [], Body = [okIsOkGetter], IsStatic = false, DeclaringType = "Ok",
            },
        });
        var okIsErrGetter = CreateBoolGetterBody(false, dummySpan);
        okDef.Properties.Add(new IrProperty
        {
            Name = "is_err",
            Type = PrimitiveType.Bool,
            Getter = new IrFunction
            {
                Name = "get_is_err", ReturnType = PrimitiveType.Bool,
                Parameters = [], Body = [okIsErrGetter], IsStatic = false, DeclaringType = "Ok",
            },
        });
        var okValueGetter = CreateFieldGetterBody("Ok", "value", dummySpan);
        okDef.Properties.Add(new IrProperty
        {
            Name = "value",
            Type = PrimitiveType.Object,
            Getter = new IrFunction
            {
                Name = "get_value", ReturnType = PrimitiveType.Object,
                Parameters = [], Body = [okValueGetter], IsStatic = false, DeclaringType = "Ok",
            },
        });

        module.Types.Insert(1, okDef);
        _typeDefs["Ok"] = okDef;

        // --- Sealed: Err : Result ---
        var errDef = new IrTypeDef
        {
            Name = "Err",
            Kind = IrTypeKind.SealedClass,
            BaseType = "Result",
        };
        errDef.Fields.Add(new IrField { Name = "value", Type = PrimitiveType.Object });

        // Constructor: Err(value)
        var errCtorBody = new IrBasicBlock { Label = "entry" };
        errCtorBody.Emit(new IrLoadThis(dummySpan));       // this
        errCtorBody.Emit(new IrLoadArg(1, dummySpan));     // value param (arg 0 = this, arg 1 = first param)
        errCtorBody.Emit(new IrStoreField("Err", "value", dummySpan));
        errCtorBody.Emit(new IrReturn(false, dummySpan));
        errDef.Constructor = new IrFunction
        {
            Name = ".ctor",
            ReturnType = PrimitiveType.Void,
            Parameters = [new IrParameter { Name = "value", Type = PrimitiveType.Object, Index = 1 }],
            Body = [errCtorBody],
            IsStatic = false,
            DeclaringType = "Err",
        };

        // Properties: is_ok (false), is_err (true), value (field)
        var errIsOkGetter = CreateBoolGetterBody(false, dummySpan);
        errDef.Properties.Add(new IrProperty
        {
            Name = "is_ok",
            Type = PrimitiveType.Bool,
            Getter = new IrFunction
            {
                Name = "get_is_ok", ReturnType = PrimitiveType.Bool,
                Parameters = [], Body = [errIsOkGetter], IsStatic = false, DeclaringType = "Err",
            },
        });
        var errIsErrGetter = CreateBoolGetterBody(true, dummySpan);
        errDef.Properties.Add(new IrProperty
        {
            Name = "is_err",
            Type = PrimitiveType.Bool,
            Getter = new IrFunction
            {
                Name = "get_is_err", ReturnType = PrimitiveType.Bool,
                Parameters = [], Body = [errIsErrGetter], IsStatic = false, DeclaringType = "Err",
            },
        });
        var errValueGetter = CreateFieldGetterBody("Err", "value", dummySpan);
        errDef.Properties.Add(new IrProperty
        {
            Name = "value",
            Type = PrimitiveType.Object,
            Getter = new IrFunction
            {
                Name = "get_value", ReturnType = PrimitiveType.Object,
                Parameters = [], Body = [errValueGetter], IsStatic = false, DeclaringType = "Err",
            },
        });

        module.Types.Insert(2, errDef);
        _typeDefs["Err"] = errDef;
    }

    private static IrBasicBlock CreateBoolGetterBody(bool value, SourceSpan span)
    {
        var block = new IrBasicBlock { Label = "entry" };
        block.Emit(new IrLoadBool(value, span));
        block.Emit(new IrReturn(true, span));
        return block;
    }

    private static IrBasicBlock CreateFieldGetterBody(string typeName, string fieldName, SourceSpan span)
    {
        var block = new IrBasicBlock { Label = "entry" };
        block.Emit(new IrLoadThis(span));
        block.Emit(new IrLoadField(typeName, fieldName, span));
        block.Emit(new IrReturn(true, span));
        return block;
    }

    /// <summary>
    /// For each class that implements an interface, check if the interface has default method
    /// implementations that the class does not override. If so, copy the default implementation
    /// into the class as an instance method.
    /// </summary>
    private void InjectDefaultInterfaceMethods(IrModule module)
    {
        foreach (var typeDef in module.Types)
        {
            if (typeDef.Kind is not (IrTypeKind.Class or IrTypeKind.Record))
                continue;

            foreach (var ifaceName in typeDef.Interfaces)
            {
                if (!_typeDefs.TryGetValue(ifaceName, out var ifaceTypeDef))
                    continue;

                foreach (var ifaceMethod in ifaceTypeDef.Methods)
                {
                    // Check if this is a default implementation (has a real body, not just IrReturn(false))
                    var totalInstructions = ifaceMethod.Body.Sum(b => b.Instructions.Count);
                    var isAbstract = totalInstructions <= 1
                        && ifaceMethod.Body.All(b => b.Instructions.All(i => i is IrReturn { HasValue: false }));

                    if (isAbstract)
                        continue;

                    // Check if the class already has this method
                    var classHasMethod = typeDef.Methods.Any(m => m.Name == ifaceMethod.Name)
                        || (typeDef.Constructor is not null && ifaceMethod.Name == ".ctor");
                    if (classHasMethod)
                        continue;

                    // Copy the default interface method into the class, re-targeting DeclaringType
                    var copiedMethod = new IrFunction
                    {
                        Name = ifaceMethod.Name,
                        ReturnType = ifaceMethod.ReturnType,
                        Parameters = ifaceMethod.Parameters,
                        Body = ifaceMethod.Body,
                        IsStatic = false,
                        IsAsync = ifaceMethod.IsAsync,
                        DeclaringType = typeDef.Name,
                        Decorators = ifaceMethod.Decorators,
                    };
                    // Copy locals
                    foreach (var local in ifaceMethod.Locals)
                        copiedMethod.Locals.Add(local);

                    typeDef.Methods.Add(copiedMethod);
                }
            }
        }
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
            Type = p.IsVarArgs
                ? new GenericInstanceType("array", [PrimitiveType.Object], typeof(object[]))
                : _typeChecker.ResolveTypeAnnotation(p.Type),
            Index = i,
            IsVarArgs = p.IsVarArgs,
        }).ToList();

        var entryBlock = new IrBasicBlock { Label = NewBlockLabel("entry") };
        var body = new List<IrBasicBlock> { entryBlock };

        // Extract decorators for attribute emission
        var decorators = ExtractDecorators(func.Decorators);

        var irFunc = new IrFunction
        {
            Name = func.Name,
            ReturnType = returnType,
            Parameters = parameters,
            Body = body,
            IsStatic = true,
            IsAsync = func.IsAsync,
            IsEntryPoint = func.Name == "main",
            Decorators = decorators,
        };

        _currentFunction = irFunc;
        _currentBlock = entryBlock;
        _localCounter = 0;
        _awaitCounter = 0;

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
            Type = p.IsVarArgs
                ? new GenericInstanceType("array", [PrimitiveType.Object], typeof(object[]))
                : _typeChecker.ResolveTypeAnnotation(p.Type),
            Index = i + 1, // +1 for implicit 'this'
            IsVarArgs = p.IsVarArgs,
        }).ToList();

        var entryBlock = new IrBasicBlock { Label = NewBlockLabel("entry") };
        var body = new List<IrBasicBlock> { entryBlock };

        // Extract decorators for attribute emission
        var decorators = ExtractDecorators(method.Decorators);

        var irFunc = new IrFunction
        {
            Name = method.Name,
            ReturnType = returnType,
            Parameters = parameters,
            Body = body,
            IsStatic = false,
            IsAsync = method.IsAsync,
            DeclaringType = declaringType,
            Decorators = decorators,
        };

        var prevFunction = _currentFunction;
        var prevBlock = _currentBlock;
        var prevDeclaringType = _currentDeclaringType;
        var prevLocalCounter = _localCounter;

        _currentFunction = irFunc;
        _currentBlock = entryBlock;
        _currentDeclaringType = declaringType;
        _localCounter = 0;
        _awaitCounter = 0;

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
            {
                var instrCountBefore = _currentBlock.Instructions.Count;
                LowerExpression(exprStmt.Expr);
                // Pop if the last instruction leaves a value (non-void call, etc.)
                if (_currentBlock.Instructions.Count > instrCountBefore)
                {
                    var lastInstr = _currentBlock.Instructions[^1];
                    if (InstructionLeavesValue(lastInstr))
                        _currentBlock.Emit(new IrPop(stmt.Span));
                }
                break;
            }

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

            case CompoundStatement compound:
                foreach (var inner in compound.Statements)
                    LowerStatement(inner);
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
                if (raise.Cause is not null)
                {
                    // raise X from Y: lower cause but drop it for now
                    // Full InnerException injection requires detecting constructor calls
                    LowerExpression(raise.Cause);
                    _currentBlock!.Emit(new IrPop(raise.Span));
                }
                _currentBlock!.Emit(new IrThrow(raise.Span));
                break;

            case AssertStatement assertStmt:
                LowerAssert(assertStmt);
                break;

            case TryStatement tryStmt:
                LowerTryStatement(tryStmt);
                break;

            case MatchStatement matchStmt:
                LowerMatchStatement(matchStmt);
                break;

            case FromImportStatement fromImport:
                ProcessFromImport(fromImport);
                break;

            case ImportStatement import:
                ProcessImport(import);
                break;

            case WithStatement withStmt:
                LowerWithStatement(withStmt);
                break;

            case YieldStatement yieldStmt:
                if (_currentBlock is not null && _currentFunction is not null)
                {
                    if (yieldStmt.Value is not null)
                    {
                        LowerExpression(yieldStmt.Value);
                        // Box value types for List<object>.Add(object)
                        var yieldType = InferExpressionType(yieldStmt.Value);
                        if (yieldType is PrimitiveType ypt && ypt.ClrType is not null && ypt.ClrType.IsValueType)
                            _currentBlock.Emit(new IrBox(ypt, yieldStmt.Span));
                    }
                    else
                    {
                        _currentBlock.Emit(new IrLoadNull(yieldStmt.Span));
                    }
                    _currentBlock.Emit(new IrYield(yieldStmt.Span));
                    _currentFunction.IsGenerator = true;
                }
                break;

            case FunctionDef:
            case ClassDef:
            case WhenStatement:
            case TypeAliasStatement:
                break; // Handled at module level or compile-time only

            default:
                _diagnostics.Warning("LEB3001", $"Unhandled statement type in lowering: {stmt.GetType().Name}", stmt.Span);
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
            // self.field = value → this.field = value (Python compatibility)
            if (member.Object is IdentifierExpr { Name: "self" } && _currentDeclaringType is not null && _currentTypeDef is not null)
            {
                var fType = _currentTypeDef.Fields.FirstOrDefault(f => f.Name == member.Member)?.Type ?? PrimitiveType.Object;
                var tempLocal = CreateLocal($"<field_tmp_{member.Member}>", fType);
                _currentBlock.Emit(new IrStoreLocal(tempLocal.Index, assign.Span));
                _currentBlock.Emit(new IrLoadThis(assign.Span));
                _currentBlock.Emit(new IrLoadLocal(tempLocal.Index, assign.Span));
                _currentBlock.Emit(new IrStoreField(_currentDeclaringType, member.Member, assign.Span));
                return;
            }

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
        else if (assign.Target is TupleExpr tupleTarget)
        {
            LowerTupleUnpacking(tupleTarget, assign);
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
                // List extend: items += [4, 5] → items.AddRange(rhs)
                if (augAssign.Op == Lexer.TokenKind.PlusAssign && IsListType(local.Type))
                {
                    _currentBlock.Emit(new IrLoadLocal(local.Index, augAssign.Span));
                    LowerExpression(augAssign.Value);
                    _currentBlock.Emit(new IrCallDotNetInstance(typeof(List<object>), "AddRange", 1, augAssign.Span));
                    return;
                }

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

                // List extend for fields
                if (augAssign.Op == Lexer.TokenKind.PlusAssign && IsListType(field.Type))
                {
                    _currentBlock.Emit(new IrLoadThis(augAssign.Span));
                    _currentBlock.Emit(new IrLoadField(_currentDeclaringType, ident.Name, augAssign.Span));
                    LowerExpression(augAssign.Value);
                    _currentBlock.Emit(new IrCallDotNetInstance(typeof(List<object>), "AddRange", 1, augAssign.Span));
                    return;
                }

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

            // List extend for new locals
            if (augAssign.Op == Lexer.TokenKind.PlusAssign && IsListType(newLocal.Type))
            {
                _currentBlock.Emit(new IrLoadLocal(newLocal.Index, augAssign.Span));
                LowerExpression(augAssign.Value);
                _currentBlock.Emit(new IrCallDotNetInstance(typeof(List<object>), "AddRange", 1, augAssign.Span));
                return;
            }

            _currentBlock.Emit(new IrLoadLocal(newLocal.Index, augAssign.Span));
            LowerExpression(augAssign.Value);
            _currentBlock.Emit(new IrBinaryOp(MapAugmentedOp(augAssign.Op), newLocal.Type, augAssign.Span));
            _currentBlock.Emit(new IrStoreLocal(newLocal.Index, augAssign.Span));
        }
        else if (augAssign.Target is MemberAccessExpr member &&
                 member.Object is IdentifierExpr { Name: "self" } &&
                 _currentDeclaringType is not null && _currentTypeDef is not null)
        {
            // self.field += value → this.field = this.field + value
            var fieldDef = _currentTypeDef.Fields.FirstOrDefault(f => f.Name == member.Member);
            var fieldType = fieldDef?.Type ?? PrimitiveType.Object;

            // Load current value: this.field
            _currentBlock.Emit(new IrLoadThis(augAssign.Span));
            _currentBlock.Emit(new IrLoadField(_currentDeclaringType, member.Member, augAssign.Span));
            // Compute new value
            LowerExpression(augAssign.Value);
            _currentBlock.Emit(new IrBinaryOp(MapAugmentedOp(augAssign.Op), fieldType, augAssign.Span));
            // Store: this.field = result
            var tempLocal = CreateLocal("<aug_self_tmp>", fieldType);
            _currentBlock.Emit(new IrStoreLocal(tempLocal.Index, augAssign.Span));
            _currentBlock.Emit(new IrLoadThis(augAssign.Span));
            _currentBlock.Emit(new IrLoadLocal(tempLocal.Index, augAssign.Span));
            _currentBlock.Emit(new IrStoreField(_currentDeclaringType, member.Member, augAssign.Span));
        }
    }

    private static bool IsListType(CulebralType type) =>
        type is GenericInstanceType git && git.Name == "list";

    /// <summary>
    /// Lower call arguments when one or more are unpacked (*args).
    /// Non-unpacked args are lowered normally. Unpacked args are stored to a temp
    /// and then individual elements are accessed by index to fill parameter slots.
    /// </summary>
    private void LowerCallWithUnpacking(List<Argument> arguments, int targetParamCount, SourceSpan span,
        List<IrParameter>? targetParams = null)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Count non-unpacked arguments
        int normalCount = arguments.Count(a => !a.IsUnpacked);
        int unpackedNeeded = targetParamCount - normalCount;

        int paramSlot = 0;
        foreach (var arg in arguments)
        {
            if (!arg.IsUnpacked)
            {
                LowerExpression(arg.Value);
                paramSlot++;
            }
            else
            {
                // Lower the iterable and store to temp
                LowerExpression(arg.Value);
                var tempLocal = CreateLocal("<unpack_tmp>", PrimitiveType.Object);
                _currentBlock.Emit(new IrStoreLocal(tempLocal.Index, span));

                // Emit index-based access for each element needed
                for (int i = 0; i < unpackedNeeded; i++)
                {
                    _currentBlock.Emit(new IrLoadLocal(tempLocal.Index, span));
                    _currentBlock.Emit(new IrLoadInt(i, span));
                    _currentBlock.Emit(new IrLoadElement(span));

                    // Unbox if target parameter is a value type (IrLoadElement returns object)
                    if (targetParams is not null && paramSlot < targetParams.Count)
                    {
                        var paramType = targetParams[paramSlot].Type;
                        if (paramType is PrimitiveType pt && pt.ClrType is not null && pt.ClrType.IsValueType)
                            _currentBlock.Emit(new IrUnbox(pt, span));
                    }
                    paramSlot++;
                }
            }
        }
    }

    private void LowerIf(IfStatement ifStmt)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        var thenLabel = NewBlockLabel("if_then");
        var elseLabel = NewBlockLabel("if_else");
        var endLabel = NewBlockLabel("if_end");

        LowerExpression(ifStmt.Condition);
        EmitTruthinessIfNeeded(ifStmt.Condition);
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
            EmitTruthinessIfNeeded(elif.Condition);
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

        var hasElse = whileStmt.ElseBody is not null;

        var condLabel = NewBlockLabel("while_cond");
        var bodyLabel = NewBlockLabel("while_body");
        var endLabel = NewBlockLabel("while_end");

        // For while-else: track whether break was hit
        IrLocal? breakFlagLocal = null;
        string? breakTargetLabel = null;
        string? elseCheckLabel = null;

        if (hasElse)
        {
            breakFlagLocal = CreateLocal("<while_break_flag>", PrimitiveType.Bool);
            _currentBlock.Emit(new IrLoadBool(false, whileStmt.Span));
            _currentBlock.Emit(new IrStoreLocal(breakFlagLocal.Index, whileStmt.Span));

            breakTargetLabel = NewBlockLabel("while_break");
            elseCheckLabel = NewBlockLabel("while_else_check");
        }

        _currentBlock.Emit(new IrBranch(condLabel, whileStmt.Span));

        var condBlock = new IrBasicBlock { Label = condLabel };
        _currentFunction.Body.Add(condBlock);
        _currentBlock = condBlock;
        LowerExpression(whileStmt.Condition);
        EmitTruthinessIfNeeded(whileStmt.Condition);
        // When condition is false: go to else check (if else exists) or end
        _currentBlock.Emit(new IrBranchIf(bodyLabel, hasElse ? elseCheckLabel! : endLabel, whileStmt.Span));

        var bodyBlock = new IrBasicBlock { Label = bodyLabel };
        _currentFunction.Body.Add(bodyBlock);
        _currentBlock = bodyBlock;

        // Break branches to breakTargetLabel (which sets flag) instead of endLabel directly
        _loopStack.Push((hasElse ? breakTargetLabel! : endLabel, condLabel));
        LowerBlock(whileStmt.Body);
        _loopStack.Pop();

        _currentBlock?.Emit(new IrBranch(condLabel, whileStmt.Span));

        if (hasElse)
        {
            // Break target: set flag = true, then branch to end
            var breakBlock = new IrBasicBlock { Label = breakTargetLabel! };
            _currentFunction.Body.Add(breakBlock);
            breakBlock.Emit(new IrLoadBool(true, whileStmt.Span));
            breakBlock.Emit(new IrStoreLocal(breakFlagLocal!.Index, whileStmt.Span));
            breakBlock.Emit(new IrBranch(endLabel, whileStmt.Span));

            // Else check: if flag is true (break hit), skip else body
            var elseCheckBlock = new IrBasicBlock { Label = elseCheckLabel! };
            _currentFunction.Body.Add(elseCheckBlock);
            _currentBlock = elseCheckBlock;
            _currentBlock.Emit(new IrLoadLocal(breakFlagLocal.Index, whileStmt.Span));
            var elseBodyLabel = NewBlockLabel("while_else_body");
            _currentBlock.Emit(new IrBranchIf(endLabel, elseBodyLabel, whileStmt.Span));

            var elseBodyBlock = new IrBasicBlock { Label = elseBodyLabel };
            _currentFunction.Body.Add(elseBodyBlock);
            _currentBlock = elseBodyBlock;
            LowerBlock(whileStmt.ElseBody!);
            _currentBlock?.Emit(new IrBranch(endLabel, whileStmt.Span));
        }

        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(endBlock);
        _currentBlock = endBlock;
    }

    private void LowerFor(ForStatement forStmt)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        var hasElse = forStmt.ElseBody is not null;

        // Desugar: for x in iterable → get enumerator, while MoveNext, x = Current
        var condLabel = NewBlockLabel("for_cond");
        var bodyLabel = NewBlockLabel("for_body");
        var endLabel = NewBlockLabel("for_end");

        // For for-else: track whether break was hit
        IrLocal? breakFlagLocal = null;
        string? breakTargetLabel = null;
        string? elseCheckLabel = null;

        if (hasElse)
        {
            breakFlagLocal = CreateLocal("<for_break_flag>", PrimitiveType.Bool);
            _currentBlock.Emit(new IrLoadBool(false, forStmt.Span));
            _currentBlock.Emit(new IrStoreLocal(breakFlagLocal.Index, forStmt.Span));

            breakTargetLabel = NewBlockLabel("for_break");
            elseCheckLabel = NewBlockLabel("for_else_check");
        }

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
        // When MoveNext returns false: go to else check (if else exists) or end
        _currentBlock.Emit(new IrBranchIf(bodyLabel, hasElse ? elseCheckLabel! : endLabel, forStmt.Span));

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

        // Break branches to breakTargetLabel (which sets flag) instead of endLabel directly
        _loopStack.Push((hasElse ? breakTargetLabel! : endLabel, condLabel));
        LowerBlock(forStmt.Body);
        _loopStack.Pop();

        _currentBlock?.Emit(new IrBranch(condLabel, forStmt.Span));

        if (hasElse)
        {
            // Break target: set flag = true, then branch to end
            var breakBlock = new IrBasicBlock { Label = breakTargetLabel! };
            _currentFunction.Body.Add(breakBlock);
            breakBlock.Emit(new IrLoadBool(true, forStmt.Span));
            breakBlock.Emit(new IrStoreLocal(breakFlagLocal!.Index, forStmt.Span));
            breakBlock.Emit(new IrBranch(endLabel, forStmt.Span));

            // Else check: if flag is true (break hit), skip else body
            var elseCheckBlock = new IrBasicBlock { Label = elseCheckLabel! };
            _currentFunction.Body.Add(elseCheckBlock);
            _currentBlock = elseCheckBlock;
            _currentBlock.Emit(new IrLoadLocal(breakFlagLocal.Index, forStmt.Span));
            var elseBodyLabel = NewBlockLabel("for_else_body");
            _currentBlock.Emit(new IrBranchIf(endLabel, elseBodyLabel, forStmt.Span));

            var elseBodyBlock = new IrBasicBlock { Label = elseBodyLabel };
            _currentFunction.Body.Add(elseBodyBlock);
            _currentBlock = elseBodyBlock;
            LowerBlock(forStmt.ElseBody!);
            _currentBlock?.Emit(new IrBranch(endLabel, forStmt.Span));
        }

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
                var binOp = MapBinaryOp(bin.Op);
                CulebralType? leftType = _typeChecker.ResolvedTypes.TryGetValue(bin.Left, out var lt) ? lt : null;
                CulebralType? rightType = _typeChecker.ResolvedTypes.TryGetValue(bin.Right, out var rt) ? rt : null;

                // Refine object types for field access on known user types
                // The type checker may return object for `other.x` even when field x is int
                if (leftType == PrimitiveType.Object)
                    leftType = RefineFieldAccessType(bin.Left) ?? leftType;
                if (rightType == PrimitiveType.Object)
                    rightType = RefineFieldAccessType(bin.Right) ?? rightType;

                // ── List concatenation: list + list ──
                if (binOp == IrBinaryOpKind.Add && leftType is GenericInstanceType { Name: "list" }
                    && rightType is GenericInstanceType { Name: "list" })
                {
                    LowerExpression(bin.Left);
                    LowerExpression(bin.Right);
                    _currentBlock!.Emit(new IrListConcat(expr.Span));
                    break;
                }

                // ── List repetition: list * int ──
                if (binOp == IrBinaryOpKind.Mul && leftType is GenericInstanceType { Name: "list" }
                    && rightType == PrimitiveType.Int)
                {
                    LowerExpression(bin.Left);
                    LowerExpression(bin.Right);
                    _currentBlock!.Emit(new IrListRepeat(expr.Span));
                    break;
                }

                // ── String repetition: str * int ──
                if (binOp == IrBinaryOpKind.Mul && leftType == PrimitiveType.Str
                    && rightType == PrimitiveType.Int)
                {
                    LowerExpression(bin.Left);
                    LowerExpression(bin.Right);
                    _currentBlock!.Emit(new IrStringRepeat(expr.Span));
                    break;
                }

                // ── Dict merge: dict | dict ──
                if (binOp == IrBinaryOpKind.BitOr
                    && leftType is GenericInstanceType { Name: "dict" }
                    && rightType is GenericInstanceType { Name: "dict" })
                {
                    LowerExpression(bin.Left);
                    LowerExpression(bin.Right);
                    _currentBlock!.Emit(new IrDictMerge(expr.Span));
                    break;
                }

                // ── User-defined operator dispatch ──
                var dunderName = BinaryOpToDunder(binOp);
                if (dunderName is not null)
                {
                    var leftTypeName = leftType?.DisplayName;
                    var (foundType, _) = FindMethodInHierarchy(leftTypeName, dunderName);
                    if (foundType is not null)
                    {
                        LowerExpression(bin.Left);
                        LowerExpression(bin.Right);
                        _currentBlock!.Emit(new IrCallMethod(foundType, dunderName, 1, expr.Span));
                        break;
                    }
                    // __ne__ fallback: use __eq__ + not if __ne__ doesn't exist
                    if (binOp == IrBinaryOpKind.NotEqual)
                    {
                        var (eqType, _) = FindMethodInHierarchy(leftTypeName, "__eq__");
                        if (eqType is not null)
                        {
                            LowerExpression(bin.Left);
                            LowerExpression(bin.Right);
                            _currentBlock!.Emit(new IrCallMethod(eqType, "__eq__", 1, expr.Span));
                            _currentBlock.Emit(new IrUnaryOp(IrUnaryOpKind.LogicalNot, expr.Span));
                            break;
                        }
                    }
                }

                var isArithmetic = binOp is IrBinaryOpKind.Add or IrBinaryOpKind.Sub or IrBinaryOpKind.Mul
                    or IrBinaryOpKind.Div or IrBinaryOpKind.IntDiv or IrBinaryOpKind.Mod or IrBinaryOpKind.Pow;

                // Determine the target numeric type for unboxing object operands
                CulebralType? numericType = null;
                if (isArithmetic && (leftType == PrimitiveType.Object || rightType == PrimitiveType.Object))
                {
                    // Use the concrete numeric type from whichever side has one, default to int
                    if (leftType is PrimitiveType lpt && lpt != PrimitiveType.Object && lpt != PrimitiveType.Str)
                        numericType = lpt;
                    else if (rightType is PrimitiveType rpt && rpt != PrimitiveType.Object && rpt != PrimitiveType.Str)
                        numericType = rpt;
                    else
                        numericType = PrimitiveType.Int; // default when both are object
                }

                var isLogical = binOp is IrBinaryOpKind.LogicalAnd or IrBinaryOpKind.LogicalOr;

                LowerExpression(bin.Left);
                // Unbox left operand if it's object and we need a numeric type
                if (numericType is not null && leftType == PrimitiveType.Object)
                    _currentBlock!.Emit(new IrUnbox(numericType, expr.Span));
                // Truthiness for and/or operands
                if (isLogical)
                    EmitTruthinessIfNeeded(bin.Left);

                LowerExpression(bin.Right);
                // Unbox right operand if it's object and we need a numeric type
                if (numericType is not null && rightType == PrimitiveType.Object)
                    _currentBlock!.Emit(new IrUnbox(numericType, expr.Span));
                // Truthiness for and/or operands
                if (isLogical)
                    EmitTruthinessIfNeeded(bin.Right);

                // Use the resolved numeric type as the operand type for the binary op
                var effectiveType = numericType ?? leftType;
                _currentBlock!.Emit(new IrBinaryOp(binOp, effectiveType, expr.Span));

                // If we unboxed to perform arithmetic, box the result back to object
                // so that the stack type matches what the type checker expects (object)
                if (numericType is PrimitiveType boxBackPt && boxBackPt.ClrType is not null && boxBackPt.ClrType.IsValueType)
                    _currentBlock.Emit(new IrBox(boxBackPt, expr.Span));
                break;
            }

            case UnaryExpr unary:
            {
                var unaryDunder = UnaryOpToDunder(unary.Op);
                var operandType = _typeChecker.ResolvedTypes.TryGetValue(unary.Operand, out var uot) ? uot : null;
                var operandTypeName = operandType?.DisplayName;
                var (unaryFoundType, _) = FindMethodInHierarchy(operandTypeName, unaryDunder);
                if (unaryDunder is not null && unaryFoundType is not null)
                {
                    LowerExpression(unary.Operand);
                    _currentBlock!.Emit(new IrCallMethod(unaryFoundType, unaryDunder, 0, expr.Span));
                    break;
                }
                LowerExpression(unary.Operand);
                if (unary.Op == Lexer.TokenKind.KwNot)
                    EmitTruthinessIfNeeded(unary.Operand);
                _currentBlock!.Emit(new IrUnaryOp(MapUnaryOp(unary.Op), expr.Span));
                break;
            }

            case CallExpr call:
                LowerCall(call);
                break;

            case MemberAccessExpr member:
            {
                // self.field → this.field (Python compatibility)
                if (member.Object is IdentifierExpr { Name: "self" } && _currentDeclaringType is not null)
                {
                    _currentBlock.Emit(new IrLoadThis(expr.Span));
                    _currentBlock.Emit(new IrLoadField(_currentDeclaringType, member.Member, expr.Span));
                    break;
                }

                var objType = _typeChecker.ResolvedTypes.TryGetValue(member.Object, out var ot) ? ot : null;

                // Named tuple member access: result.name → array index access
                if (objType is TupleCulebralType tupleType)
                {
                    for (int i = 0; i < tupleType.Names.Length; i++)
                    {
                        if (tupleType.Names[i] == member.Member)
                        {
                            LowerExpression(member.Object);
                            _currentBlock.Emit(new IrTupleElement(i, expr.Span));
                            break;
                        }
                    }
                    break;
                }

                // .NET static property/field: Math.pi, Console.out
                if (member.Object is IdentifierExpr objId && ResolveDotNetType(objId.Name) is Type staticDotNet)
                {
                    var pascalName = DotNetTypeResolver.SnakeToPascal(member.Member);
                    var prop = _typeChecker.DotNetResolver.ResolveProperty(staticDotNet, member.Member, isStatic: true);
                    if (prop is not null)
                    {
                        _currentBlock.Emit(new IrLoadDotNetProperty(staticDotNet, pascalName, true, expr.Span));
                        break;
                    }
                    var field = _typeChecker.DotNetResolver.ResolveField(staticDotNet, member.Member, isStatic: true);
                    if (field is not null)
                    {
                        _currentBlock.Emit(new IrLoadDotNetField(staticDotNet, pascalName, true, expr.Span));
                        break;
                    }
                    // Could be a nested type or method ref — fall through
                }

                // .NET instance property: response.status_code
                if (objType is DotNetType dotNetInstance)
                {
                    LowerExpression(member.Object);
                    var pascalName = DotNetTypeResolver.SnakeToPascal(member.Member);
                    _currentBlock.Emit(new IrLoadDotNetProperty(dotNetInstance.ClrBackingType, pascalName, false, expr.Span));
                    break;
                }

                // .NET namespace member access: io.File → don't emit, used at call site
                if (objType is DotNetNamespaceType)
                {
                    // This is resolved at the call site (LowerCall handles the chain)
                    // Don't emit anything — the call handler will resolve the full chain
                    break;
                }

                // Culebral user type member access
                LowerExpression(member.Object);
                var typeNameStr = objType?.DisplayName ?? "object";

                if (_typeDefs.TryGetValue(typeNameStr, out var memberTypeDef) &&
                    memberTypeDef.Properties.Any(p => p.Name == member.Member))
                {
                    _currentBlock.Emit(new IrCallMethod(typeNameStr, $"get_{member.Member}", 0, expr.Span));
                }
                else
                {
                    _currentBlock.Emit(new IrLoadField(typeNameStr, member.Member, expr.Span));
                }
                break;
            }

            case IndexExpr index:
            {
                // Check if the object is a user type with __getitem__
                var indexObjType = ResolveExpressionType(index.Object);
                var indexTypeName = indexObjType?.DisplayName;
                if (indexTypeName is not null && _typeDefs.TryGetValue(indexTypeName, out var indexTypeDef) &&
                    indexTypeDef.Methods.Any(m => m.Name == "__getitem__"))
                {
                    LowerExpression(index.Object);
                    LowerExpression(index.Index);
                    _currentBlock.Emit(new IrCallMethod(indexTypeName, "__getitem__", 1, expr.Span));
                }
                else
                {
                    LowerExpression(index.Object);
                    LowerExpression(index.Index);
                    _currentBlock.Emit(new IrLoadElement(expr.Span));
                }
                break;
            }

            case ListExpr list:
                LowerListExpr(list);
                break;

            case TupleExpr tuple:
                foreach (var elem in tuple.Elements)
                {
                    LowerExpression(elem);
                    // Box value types for object[] storage
                    var elemType = _typeChecker.ResolvedTypes.TryGetValue(elem, out var et) ? et : null;
                    if (elemType is PrimitiveType tupleElemPt && tupleElemPt.ClrType is not null && tupleElemPt.ClrType.IsValueType)
                        _currentBlock.Emit(new IrBox(tupleElemPt, elem.Span));
                }
                _currentBlock.Emit(new IrNewTuple(tuple.Elements.Count, expr.Span));
                break;

            case FStringExpr fstr:
                LowerFString(fstr);
                break;

            case ConditionalExpr cond:
                LowerConditional(cond);
                break;

            case LambdaExpr lambda:
                LowerLambdaExpr(lambda);
                break;

            case AwaitExpr awaitExpr:
                LowerExpression(awaitExpr.Operand);
                // Determine if the await produces a value (Task<T> vs Task)
                var awaitType = _typeChecker.ResolvedTypes.TryGetValue(awaitExpr.Operand, out var at) ? at : PrimitiveType.Object;
                bool awaitHasResult = awaitType != PrimitiveType.Void;
                _currentBlock.Emit(new IrAwait(awaitHasResult, _awaitCounter++, awaitExpr.Span));
                // IrAwait leaves an object on the stack (from Task<object>.Result);
                // unbox to the expected value type if needed
                if (awaitHasResult && awaitType is PrimitiveType pt && pt.ClrBackingType.IsValueType)
                    _currentBlock.Emit(new IrUnbox(awaitType, awaitExpr.Span));
                break;

            case ListComprehension comp:
                LowerListComprehension(comp);
                break;

            case IsExpr isExpr:
                LowerIsExpr(isExpr);
                break;

            case InExpr inExpr:
                LowerInExpr(inExpr);
                break;

            case SetExpr setExpr:
                LowerSetExpr(setExpr);
                break;

            case DictExpr dict:
                LowerDictExpr(dict);
                break;

            case DictComprehension dictComp:
                LowerDictComprehension(dictComp);
                break;

            case SetComprehension setComp:
                LowerSetComprehension(setComp);
                break;

            case GeneratorExpr genExpr:
                LowerGeneratorExpr(genExpr);
                break;

            case TypeCastExpr cast:
                LowerExpression(cast.Expr);
                var castTypeName = cast.Type is SimpleType castSt ? castSt.Name : "object";
                _currentBlock.Emit(new IrCastClass(castTypeName, expr.Span));
                break;

            case SliceExpr slice:
                LowerSliceExpr(slice);
                break;

            case WithExpr withExpr:
                LowerWithExpr(withExpr);
                break;

            default:
                _currentBlock.Emit(new IrLoadNull(expr.Span));
                break;
        }
    }

    private void LowerIdentifier(IdentifierExpr ident)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // 'self' in a class method → this reference (Python compatibility)
        if (ident.Name == "self" && _currentDeclaringType is not null)
        {
            _currentBlock.Emit(new IrLoadThis(ident.Span));
            return;
        }

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

    /// <summary>
    /// Resolve a Culebral expression (from index syntax) to a System.Type for generic method type arguments.
    /// Handles: IdentifierExpr for simple types, and MemberAccessExpr for fully-qualified .NET types.
    /// </summary>
    private Type? ResolveExprToClrType(Expression expr)
    {
        if (expr is IdentifierExpr ident)
        {
            // Built-in primitives
            var clrType = ident.Name switch
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
            };
            if (clrType is not null) return clrType;

            // Imported .NET type
            if (_importedDotNetTypes.TryGetValue(ident.Name, out var dotNetType))
                return dotNetType;

            // User-defined type — resolve to its TypeBuilder at emission time
            // For now, map to object (type erasure for user types in generic context)
            return typeof(object);
        }

        if (expr is MemberAccessExpr member)
        {
            // Namespace.Type chain, e.g., io.File → System.IO.File
            if (member.Object is IdentifierExpr nsId && _namespaceAliases.ContainsKey(nsId.Name))
            {
                var resolved = ResolveDotNetChain(nsId.Name, member.Member);
                if (resolved is not null) return resolved;
            }

            // Direct fully-qualified name attempt
            if (member.Object is IdentifierExpr objId)
            {
                var resolved = _typeChecker.DotNetResolver.ResolveType($"{objId.Name}.{member.Member}");
                if (resolved is not null) return resolved;
            }
        }

        return null;
    }

    /// <summary>
    /// Try to resolve the CLR type of the receiver for extension method lookup.
    /// </summary>
    private Type? ResolveReceiverClrType(Expression objExpr)
    {
        var objType = _typeChecker.ResolvedTypes.TryGetValue(objExpr, out var ot) ? ot : null;
        return objType switch
        {
            DotNetType dt => dt.ClrBackingType,
            PrimitiveType pt => pt.ClrType,
            GenericInstanceType git => git.ClrBackingType,
            _ => null,
        };
    }

    private void LowerCall(CallExpr call)
    {
        if (_currentBlock is null) return;

        // Generic method call: method[TypeArg](args) → CallExpr(IndexExpr(callee, typeArg), args)
        if (call.Callee is IndexExpr { Object: var genericCallee, Index: var typeArgExpr })
        {
            // Resolve type arguments — single or multiple (TupleExpr)
            Type[]? typeArgs = null;
            if (typeArgExpr is TupleExpr tupleTypeArgs)
            {
                var resolved = new List<Type>();
                foreach (var elem in tupleTypeArgs.Elements)
                {
                    var t = ResolveExprToClrType(elem);
                    if (t is null) { resolved = null; break; }
                    resolved.Add(t);
                }
                typeArgs = resolved?.ToArray();
            }
            else
            {
                var typeArg = ResolveExprToClrType(typeArgExpr);
                if (typeArg is not null)
                    typeArgs = [typeArg];
            }

            if (typeArgs is not null)
            {
                // Static generic: Type.method[T](args)
                if (genericCallee is MemberAccessExpr genMember)
                {
                    var methodName = DotNetTypeResolver.SnakeToPascal(genMember.Member);

                    // Direct static: File.method[T](args)
                    if (genMember.Object is IdentifierExpr genObjId && ResolveDotNetType(genObjId.Name) is Type staticType)
                    {
                        foreach (var arg in call.Arguments)
                            LowerExpression(arg.Value);
                        _currentBlock.Emit(new IrCallDotNetGenericStatic(
                            staticType, methodName, call.Arguments.Count, typeArgs, call.Span));
                        return;
                    }

                    // Namespace chain: ns.Type.method[T](args)
                    if (genMember.Object is MemberAccessExpr outerMember2 &&
                        outerMember2.Object is IdentifierExpr nsId2 &&
                        _namespaceAliases.ContainsKey(nsId2.Name))
                    {
                        var chainType = ResolveDotNetChain(nsId2.Name, outerMember2.Member);
                        if (chainType is not null)
                        {
                            foreach (var arg in call.Arguments)
                                LowerExpression(arg.Value);
                            _currentBlock.Emit(new IrCallDotNetGenericStatic(
                                chainType, methodName, call.Arguments.Count, typeArgs, call.Span));
                            return;
                        }
                    }

                    // Instance generic: obj.method[T](args)
                    var genObjType = _typeChecker.ResolvedTypes.TryGetValue(genMember.Object, out var got) ? got : null;
                    if (genObjType is DotNetType dotNetGenType)
                    {
                        LowerExpression(genMember.Object);
                        foreach (var arg in call.Arguments)
                            LowerExpression(arg.Value);
                        _currentBlock.Emit(new IrCallDotNetGenericInstance(
                            dotNetGenType.ClrBackingType, methodName, call.Arguments.Count, typeArgs, call.Span));
                        return;
                    }
                }

                // Standalone generic: Type[T1, T2](args) — e.g., Dictionary[str, int]()
                if (genericCallee is IdentifierExpr genFuncId && ResolveDotNetType(genFuncId.Name) is Type genStaticType)
                {
                    // If the resolved type is a generic type definition, make the concrete generic type
                    var concreteType = genStaticType;
                    if (genStaticType.IsGenericTypeDefinition && typeArgs.Length == genStaticType.GetGenericArguments().Length)
                        concreteType = genStaticType.MakeGenericType(typeArgs);

                    foreach (var arg in call.Arguments)
                        LowerExpression(arg.Value);
                    _currentBlock.Emit(new IrNewDotNetObj(concreteType, call.Arguments.Count, call.Span));
                    return;
                }
            }
        }

        // Handle built-in functions and constructor calls
        if (call.Callee is IdentifierExpr ident)
        {
            // Special handling for print() — supports multiple args and named parameters
            if (ident.Name == "print")
            {
                string? sep = null;
                string? end = null;
                bool flush = false;
                bool useStderr = false;

                // Separate positional and named arguments
                var positionalArgs = new List<Argument>();
                foreach (var arg in call.Arguments)
                {
                    if (arg.Name is null)
                    {
                        positionalArgs.Add(arg);
                    }
                    else
                    {
                        switch (arg.Name)
                        {
                            case "sep":
                                if (arg.Value is StringLiteralExpr sepLit) sep = sepLit.Value;
                                break;
                            case "end":
                                if (arg.Value is StringLiteralExpr endLit) end = endLit.Value;
                                break;
                            case "flush":
                                if (arg.Value is BoolLiteralExpr flushLit) flush = flushLit.Value;
                                break;
                            case "file":
                                if (arg.Value is IdentifierExpr fileIdent && fileIdent.Name == "stderr")
                                    useStderr = true;
                                break;
                        }
                    }
                }

                // Lower positional arguments onto the stack
                foreach (var arg in positionalArgs)
                    LowerExpression(arg.Value);

                _currentBlock.Emit(new IrPrint(positionalArgs.Count, sep, end, flush, useStderr, call.Span));
                return;
            }

            // cast(expr, Type) → TypeCastExpr lowering
            if (ident.Name == "cast" && call.Arguments.Count == 2)
            {
                LowerExpression(call.Arguments[0].Value);
                // Second argument should be a type name identifier
                var typeArg = call.Arguments[1].Value;
                var castTypeName = typeArg switch
                {
                    IdentifierExpr typeId => typeId.Name,
                    MemberAccessExpr ma => $"{ExtractExprName(ma.Object)}.{ma.Member}",
                    _ => "object",
                };
                _currentBlock.Emit(new IrCastClass(castTypeName, call.Span));
                return;
            }

            var builtins = new HashSet<string>
            {
                "len", "range", "int", "float", "str", "bool",
                "sorted", "abs", "min", "max", "type", "isinstance",
                "enumerate", "zip", "map", "filter", "open",
                "input", "round", "chr", "ord",
                "all", "any", "sum", "list", "dict", "set", "hash", "reversed",
                "hex", "bin", "oct", "divmod", "pow", "repr", "format", "tuple",
                "assert_equal", "assert_not_equal",
            };

            if (builtins.Contains(ident.Name))
            {
                foreach (var arg in call.Arguments)
                    LowerExpression(arg.Value);
                _currentBlock.Emit(new IrCallBuiltin(ident.Name, call.Arguments.Count, call.Span));
                return;
            }

            // Constructor call: TypeName(args) → newobj .ctor (Culebral types)
            if (_knownTypes.Contains(ident.Name))
            {
                foreach (var arg in call.Arguments)
                    LowerExpression(arg.Value);
                _currentBlock.Emit(new IrNewObj(ident.Name, call.Arguments.Count, call.Span));
                return;
            }

            // .NET type constructor: HttpClient() → newobj
            var dotNetType = ResolveDotNetType(ident.Name);
            if (dotNetType is not null)
            {
                foreach (var arg in call.Arguments)
                    LowerExpression(arg.Value);
                _currentBlock.Emit(new IrNewDotNetObj(dotNetType, call.Arguments.Count, call.Span));
                return;
            }

            // Regular function call — check for varargs and fill in default args if needed
            if (_functionDefs.TryGetValue(ident.Name, out var calledFunc) &&
                calledFunc.Parameters.Any(p => p.IsVarArgs))
            {
                // Find the varargs parameter index
                var varArgsIndex = calledFunc.Parameters.FindIndex(p => p.IsVarArgs);
                // Lower non-varargs arguments
                for (int i = 0; i < Math.Min(varArgsIndex, call.Arguments.Count); i++)
                    LowerExpression(call.Arguments[i].Value);
                // Collect excess arguments into an array
                var excessCount = Math.Max(0, call.Arguments.Count - varArgsIndex);
                for (int i = varArgsIndex; i < call.Arguments.Count; i++)
                {
                    LowerExpression(call.Arguments[i].Value);
                    // Box value types for object[]
                    var argType = _typeChecker.ResolvedTypes.TryGetValue(call.Arguments[i].Value, out var at) ? at : null;
                    if (argType is PrimitiveType apt && apt.ClrType is not null && apt.ClrType.IsValueType)
                        _currentBlock.Emit(new IrBox(apt, call.Span));
                }
                _currentBlock.Emit(new IrNewArrayFromStack(excessCount, call.Span));
                _currentBlock.Emit(new IrCall(ident.Name, varArgsIndex + 1, true, call.Span));
            }
            else if (_functionDefs.ContainsKey(ident.Name))
            {
                // Check for call-site unpacking: f(*args)
                if (call.Arguments.Any(a => a.IsUnpacked) &&
                    _functionDefs.TryGetValue(ident.Name, out var targetFunc))
                {
                    // Resolve target function's parameter types for unboxing
                    var targetParams = targetFunc.Parameters.Select((p, i) => new IrParameter
                    {
                        Name = p.Name,
                        Type = _typeChecker.ResolveTypeAnnotation(p.Type),
                        Index = i,
                    }).ToList();
                    LowerCallWithUnpacking(call.Arguments, targetFunc.Parameters.Count, call.Span, targetParams);
                    _currentBlock.Emit(new IrCall(ident.Name, targetFunc.Parameters.Count, true, call.Span));
                }
                else
                {
                    foreach (var arg in call.Arguments)
                        LowerExpression(arg.Value);
                    EmitDefaultArgs(ident.Name, call.Arguments.Count, call.Span);
                    var totalArgs = GetTotalArgCount(ident.Name, call.Arguments.Count);
                    _currentBlock.Emit(new IrCall(ident.Name, totalArgs, true, call.Span));
                }
            }
            else
            {
                // Not a known function — could be a delegate in a local/parameter.
                // Load the delegate value, then the arguments (boxed for object params), then invoke.
                LowerExpression(call.Callee);
                foreach (var arg in call.Arguments)
                {
                    LowerExpression(arg.Value);
                    // Delegate params are typed as object, so box value types
                    var argType = _typeChecker.ResolvedTypes.TryGetValue(arg.Value, out var at) ? at : null;
                    if (argType is PrimitiveType pt && pt.ClrType is not null && pt.ClrType.IsValueType)
                        _currentBlock!.Emit(new IrBox(pt, call.Span));
                }
                _currentBlock!.Emit(new IrInvokeDelegate(call.Arguments.Count, call.Span));
            }
            return;
        }

        // Method call: obj.method(args)
        if (call.Callee is MemberAccessExpr member)
        {
            var objType = _typeChecker.ResolvedTypes.TryGetValue(member.Object, out var ot) ? ot : null;
            var resolver = _typeChecker.DotNetResolver;
            var originalName = member.Member;
            var methodName = DotNetTypeResolver.SnakeToPascal(originalName);

            // .NET static method: File.read_all_text(...) — don't load the type as a value
            if (member.Object is IdentifierExpr objIdent && ResolveDotNetType(objIdent.Name) is Type staticType)
            {
                // Resolve actual method name (original first, then PascalCase)
                var staticMethod = resolver.ResolveMethod(staticType, originalName, call.Arguments.Count, isStatic: true);
                var resolvedStaticName = staticMethod?.Name ?? methodName;
                foreach (var arg in call.Arguments)
                    LowerExpression(arg.Value);
                _currentBlock.Emit(new IrCallDotNetStatic(staticType, resolvedStaticName, call.Arguments.Count, call.Span));
                return;
            }

            // .NET namespace chain: io.File.read_all_text(...)
            if (member.Object is MemberAccessExpr outerMember &&
                outerMember.Object is IdentifierExpr nsIdent &&
                _namespaceAliases.ContainsKey(nsIdent.Name))
            {
                var chainType = ResolveDotNetChain(nsIdent.Name, outerMember.Member);
                if (chainType is not null)
                {
                    var chainMethod = resolver.ResolveMethod(chainType, originalName, call.Arguments.Count, isStatic: true);
                    var resolvedChainName = chainMethod?.Name ?? methodName;
                    foreach (var arg in call.Arguments)
                        LowerExpression(arg.Value);
                    _currentBlock.Emit(new IrCallDotNetStatic(chainType, resolvedChainName, call.Arguments.Count, call.Span));
                    return;
                }
            }

            // .NET instance method: obj.get_async(...) where obj is a .NET type instance
            if (objType is DotNetType dotNetObjType)
            {
                // Check if the method actually exists on the type before emitting an instance call.
                // Try original name first (exact match), then snake_case→PascalCase conversion.
                var resolver2 = _typeChecker.DotNetResolver;
                var instanceMethod = resolver2.ResolveMethod(dotNetObjType.ClrBackingType, originalName, call.Arguments.Count, isStatic: false);
                var resolvedMethodName = instanceMethod?.Name ?? methodName;
                if (instanceMethod is not null)
                {
                    LowerExpression(member.Object);
                    foreach (var arg in call.Arguments)
                        LowerExpression(arg.Value);
                    _currentBlock.Emit(new IrCallDotNetInstance(dotNetObjType.ClrBackingType, resolvedMethodName, call.Arguments.Count, call.Span));
                    return;
                }
                // Method not found on the type — fall through to extension method check below
            }

            // Extension method check: obj.method(args) where method is an extension on a known type
            var receiverClrType = ResolveReceiverClrType(member.Object);
            if (receiverClrType is not null && _extensionMethodSources.Count > 0)
            {
                var extResolver = _typeChecker.DotNetResolver;
                foreach (var extSource in _extensionMethodSources)
                {
                    // Try non-generic extension first
                    var extMethod = extResolver.ResolveExtensionMethod(
                        extSource, receiverClrType, member.Member, call.Arguments.Count, isGeneric: false);
                    if (extMethod is not null)
                    {
                        // Extension method: emit receiver (as first arg) + explicit args → static call
                        LowerExpression(member.Object);
                        foreach (var arg in call.Arguments)
                            LowerExpression(arg.Value);
                        _currentBlock.Emit(new IrCallExtensionMethod(
                            extSource, methodName, call.Arguments.Count, null, call.Span));
                        return;
                    }

                    // Try generic extension (auto-infer type args from receiver)
                    extMethod = extResolver.ResolveExtensionMethod(
                        extSource, receiverClrType, member.Member, call.Arguments.Count, isGeneric: true);
                    if (extMethod is not null)
                    {
                        LowerExpression(member.Object);
                        foreach (var arg in call.Arguments)
                            LowerExpression(arg.Value);

                        // Infer the type argument from the receiver's generic interface
                        var typeArgs = InferExtensionTypeArgs(extMethod, receiverClrType);
                        _currentBlock.Emit(new IrCallExtensionMethod(
                            extSource, methodName, call.Arguments.Count, typeArgs, call.Span));
                        return;
                    }
                }
            }

            // DotNetType fallback: emit instance call even if method wasn't found above
            // (the emitter will produce a diagnostic for unresolvable methods)
            if (objType is DotNetType dotNetObjTypeFallback)
            {
                LowerExpression(member.Object);
                foreach (var arg in call.Arguments)
                    LowerExpression(arg.Value);
                _currentBlock.Emit(new IrCallDotNetInstance(dotNetObjTypeFallback.ClrBackingType, methodName, call.Arguments.Count, call.Span));
                return;
            }

            // Culebral user-defined type method call
            LowerExpression(member.Object);
            foreach (var arg in call.Arguments)
                LowerExpression(arg.Value);

            var typeNameStr = objType?.DisplayName;
            if (typeNameStr is not null && _knownTypes.Contains(typeNameStr))
            {
                _currentBlock.Emit(new IrCallMethod(typeNameStr, member.Member, call.Arguments.Count, call.Span));
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
            // Box value types for List<object>.Add(object)
            var elemType = _typeChecker.ResolvedTypes.TryGetValue(elem, out var et) ? et : null;
            if (elemType is PrimitiveType pt && pt.ClrType is not null && pt.ClrType.IsValueType)
                _currentBlock.Emit(new IrBox(pt, list.Span));
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
                    if (interp.FormatSpec is not null)
                    {
                        // Box value type for String.Format(string, object)
                        var fmtExprType = _typeChecker.ResolvedTypes.TryGetValue(interp.Expr, out var fet)
                            ? fet : PrimitiveType.Object;
                        if (fmtExprType is PrimitiveType fpt && fpt.ClrType is not null && fpt.ClrType.IsValueType)
                            _currentBlock.Emit(new IrBox(fpt, fstr.Span));
                        // Save value, push format string, push value — String.Format(fmt, obj)
                        var fmtTmp = CreateLocal("<fmt_val>", PrimitiveType.Object);
                        _currentBlock.Emit(new IrStoreLocal(fmtTmp.Index, fstr.Span));
                        _currentBlock.Emit(new IrLoadString($"{{0:{interp.FormatSpec}}}", fstr.Span));
                        _currentBlock.Emit(new IrLoadLocal(fmtTmp.Index, fstr.Span));
                        _currentBlock.Emit(new IrCallDotNetStatic(typeof(string), "Format", 2, fstr.Span));
                    }
                    else
                    {
                        var exprType = _typeChecker.ResolvedTypes.TryGetValue(interp.Expr, out var et)
                            ? et : PrimitiveType.Object;
                        _currentBlock.Emit(new IrToString(exprType, fstr.Span));
                    }
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

        // Desugar: [expr for x in iter1 if cond1 for y in iter2 if cond2 ...]
        // → list = new List(); nested loops with innermost adding element
        var listLocal = CreateLocal("<comp_list>", PrimitiveType.Object);
        _currentBlock.Emit(new IrNewObj("System.Collections.Generic.List`1", 0, comp.Span));
        _currentBlock.Emit(new IrStoreLocal(listLocal.Index, comp.Span));

        var endLabel = EmitComprehensionClauses(comp.Clauses, comp.Span, () =>
        {
            _currentBlock!.Emit(new IrLoadLocal(listLocal.Index, comp.Span));
            LowerExpression(comp.Element);
            _currentBlock.Emit(new IrCallVirtual("Add", 1, comp.Span));
        });

        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(endBlock);
        _currentBlock = endBlock;

        _currentBlock.Emit(new IrLoadLocal(listLocal.Index, comp.Span));
    }

    private void LowerSetExpr(SetExpr setExpr)
    {
        if (_currentBlock is null) return;

        // Create a new HashSet<object> and add elements
        _currentBlock.Emit(new IrNewObj("System.Collections.Generic.HashSet`1", 0, setExpr.Span));

        foreach (var elem in setExpr.Elements)
        {
            _currentBlock.Emit(new IrDup(setExpr.Span));
            LowerExpression(elem);
            // Box value types for HashSet<object>.Add(object)
            var elemType = _typeChecker.ResolvedTypes.TryGetValue(elem, out var et) ? et : null;
            if (elemType is PrimitiveType pt && pt.ClrType is not null && pt.ClrType.IsValueType)
                _currentBlock.Emit(new IrBox(pt, setExpr.Span));
            _currentBlock.Emit(new IrCallVirtual("Add", 1, setExpr.Span));
            // HashSet.Add returns bool — discard it
            _currentBlock.Emit(new IrPop(setExpr.Span));
        }
    }

    private void LowerDictExpr(DictExpr dict)
    {
        if (_currentBlock is null) return;

        // Create a new Dictionary<object, object> and add entries
        _currentBlock.Emit(new IrNewObj("System.Collections.Generic.Dictionary`2", 0, dict.Span));

        foreach (var (key, value) in dict.Entries)
        {
            _currentBlock.Emit(new IrDup(dict.Span));
            LowerExpression(key);
            // Box value-type keys
            var keyType = _typeChecker.ResolvedTypes.TryGetValue(key, out var kt) ? kt : null;
            if (keyType is PrimitiveType kpt && kpt.ClrType is not null && kpt.ClrType.IsValueType)
                _currentBlock.Emit(new IrBox(kpt, dict.Span));
            LowerExpression(value);
            // Box value-type values
            var valType = _typeChecker.ResolvedTypes.TryGetValue(value, out var vt) ? vt : null;
            if (valType is PrimitiveType vpt && vpt.ClrType is not null && vpt.ClrType.IsValueType)
                _currentBlock.Emit(new IrBox(vpt, dict.Span));
            _currentBlock.Emit(new IrCallVirtual("Add", 2, dict.Span));
        }
    }

    private void LowerDictComprehension(DictComprehension dictComp)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Desugar: {kExpr: vExpr for x in iterable if cond ...}
        // → dict = new Dictionary(); nested loops with innermost adding entry
        var dictLocal = CreateLocal("<dictcomp>", PrimitiveType.Object);
        _currentBlock.Emit(new IrNewObj("System.Collections.Generic.Dictionary`2", 0, dictComp.Span));
        _currentBlock.Emit(new IrStoreLocal(dictLocal.Index, dictComp.Span));

        var endLabel = EmitComprehensionClauses(dictComp.Clauses, dictComp.Span, () =>
        {
            _currentBlock!.Emit(new IrLoadLocal(dictLocal.Index, dictComp.Span));
            LowerExpression(dictComp.Key);
            LowerExpression(dictComp.Value);
            _currentBlock.Emit(new IrCallVirtual("Add", 2, dictComp.Span));
        });

        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(endBlock);
        _currentBlock = endBlock;

        _currentBlock.Emit(new IrLoadLocal(dictLocal.Index, dictComp.Span));
    }

    private void LowerSetComprehension(SetComprehension setComp)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Desugar: {expr for x in iterable if cond ...}
        // → s = new HashSet(); nested loops with innermost adding element
        var setLocal = CreateLocal("<setcomp>", PrimitiveType.Object);
        _currentBlock.Emit(new IrNewObj("System.Collections.Generic.HashSet`1", 0, setComp.Span));
        _currentBlock.Emit(new IrStoreLocal(setLocal.Index, setComp.Span));

        var endLabel = EmitComprehensionClauses(setComp.Clauses, setComp.Span, () =>
        {
            _currentBlock!.Emit(new IrLoadLocal(setLocal.Index, setComp.Span));
            LowerExpression(setComp.Element);
            // Box value types for HashSet<object>.Add(object)
            var elemType = _typeChecker.ResolvedTypes.TryGetValue(setComp.Element, out var et) ? et : null;
            if (elemType is PrimitiveType pt && pt.ClrType is not null && pt.ClrType.IsValueType)
                _currentBlock.Emit(new IrBox(pt, setComp.Span));
            _currentBlock.Emit(new IrCallVirtual("Add", 1, setComp.Span));
            _currentBlock.Emit(new IrPop(setComp.Span)); // discard bool from HashSet.Add
        });

        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(endBlock);
        _currentBlock = endBlock;

        _currentBlock.Emit(new IrLoadLocal(setLocal.Index, setComp.Span));
    }

    private void LowerGeneratorExpr(GeneratorExpr genExpr)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Desugar generator expression to eager List<object> (same as list comprehension).
        // True lazy IEnumerable<T> generation is a future optimization.
        var listLocal = CreateLocal("<genexpr>", PrimitiveType.Object);
        _currentBlock.Emit(new IrNewObj("System.Collections.Generic.List`1", 0, genExpr.Span));
        _currentBlock.Emit(new IrStoreLocal(listLocal.Index, genExpr.Span));

        var endLabel = EmitComprehensionClauses(genExpr.Clauses, genExpr.Span, () =>
        {
            _currentBlock!.Emit(new IrLoadLocal(listLocal.Index, genExpr.Span));
            LowerExpression(genExpr.Element);
            _currentBlock.Emit(new IrCallVirtual("Add", 1, genExpr.Span));
        });

        var endBlock = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(endBlock);
        _currentBlock = endBlock;

        _currentBlock.Emit(new IrLoadLocal(listLocal.Index, genExpr.Span));
    }

    /// <summary>
    /// Emits nested iteration loops for comprehension clauses.
    /// Each clause becomes a GetEnumerator/MoveNext loop, with subsequent clauses nested inside.
    /// The innermost body invokes <paramref name="emitBody"/> to add the element.
    /// Returns the end label that the caller should use to create the final end block.
    /// </summary>
    private string EmitComprehensionClauses(List<ComprehensionClause> clauses, SourceSpan span, Action emitBody)
    {
        // Track the condition labels for each clause level so the innermost body branches back
        // to the innermost clause's condition, and each clause end branches back to its own condition.
        var condLabels = new List<string>();
        var endLabels = new List<string>();

        // Open each clause level
        for (int i = 0; i < clauses.Count; i++)
        {
            var clause = clauses[i];

            LowerExpression(clause.Iterable);
            var enumeratorLocal = CreateLocal($"<comp_enum_{i}>", PrimitiveType.Object);
            _currentBlock!.Emit(new IrCallVirtual("GetEnumerator", 0, span));
            _currentBlock.Emit(new IrStoreLocal(enumeratorLocal.Index, span));

            var condLabel = NewBlockLabel($"comp_cond_{i}");
            var bodyLabel = NewBlockLabel($"comp_body_{i}");
            var endLabel = NewBlockLabel($"comp_end_{i}");
            condLabels.Add(condLabel);
            endLabels.Add(endLabel);

            _currentBlock.Emit(new IrBranch(condLabel, span));

            var condBlock = new IrBasicBlock { Label = condLabel };
            _currentFunction!.Body.Add(condBlock);
            _currentBlock = condBlock;
            _currentBlock.Emit(new IrLoadLocal(enumeratorLocal.Index, span));
            _currentBlock.Emit(new IrCallVirtual("MoveNext", 0, span));
            _currentBlock.Emit(new IrBranchIf(bodyLabel, endLabel, span));

            var bodyBlock = new IrBasicBlock { Label = bodyLabel };
            _currentFunction.Body.Add(bodyBlock);
            _currentBlock = bodyBlock;

            var varLocal = GetOrCreateLocal(clause.Variable, span);
            _currentBlock.Emit(new IrLoadLocal(enumeratorLocal.Index, span));
            _currentBlock.Emit(new IrCallVirtual("get_Current", 0, span));
            _currentBlock.Emit(new IrStoreLocal(varLocal.Index, span));

            // Condition filter: if condition fails, branch back to this clause's condition
            if (clause.Condition is not null)
            {
                var filterPassLabel = NewBlockLabel($"comp_pass_{i}");
                LowerExpression(clause.Condition);
                _currentBlock.Emit(new IrBranchIf(filterPassLabel, condLabel, span));
                var filterPassBlock = new IrBasicBlock { Label = filterPassLabel };
                _currentFunction.Body.Add(filterPassBlock);
                _currentBlock = filterPassBlock;
            }
        }

        // Emit the innermost body (add element)
        emitBody();

        // Branch back to the innermost clause's condition
        _currentBlock!.Emit(new IrBranch(condLabels[^1], span));

        // Close inner clause levels in reverse order: each end block branches back to outer condition
        // Skip the outermost (i=0) — the caller creates that end block.
        for (int i = clauses.Count - 1; i >= 1; i--)
        {
            var endBlock = new IrBasicBlock { Label = endLabels[i] };
            _currentFunction!.Body.Add(endBlock);
            _currentBlock = endBlock;

            // Branch back to the outer clause's condition
            _currentBlock.Emit(new IrBranch(condLabels[i - 1], span));
        }

        // Return the outermost end label — caller creates the final end block
        return endLabels[0];
    }

    private void LowerWithStatement(WithStatement withStmt)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // For each with-item: lower context expr, store to local, wrap body in try-finally with Dispose
        // We nest the items: with a, b → try { a } finally { a.Dispose() } wrapping try { b } finally { b.Dispose() }
        // Simplified: emit all context locals, then a single try-finally that disposes all
        var contextLocals = new List<IrLocal>();

        foreach (var item in withStmt.Items)
        {
            LowerExpression(item.ContextExpr);
            var ctxLocal = CreateLocal($"<with_ctx_{contextLocals.Count}>", PrimitiveType.Object);
            _currentBlock.Emit(new IrStoreLocal(ctxLocal.Index, item.Span));
            contextLocals.Add(ctxLocal);

            // If variable name exists, create/assign named local
            if (item.Variable is not null)
            {
                var namedLocal = GetOrCreateLocal(item.Variable, item.Span);
                _currentBlock.Emit(new IrLoadLocal(ctxLocal.Index, item.Span));
                _currentBlock.Emit(new IrStoreLocal(namedLocal.Index, item.Span));
            }
        }

        // Begin exception block (try-finally)
        _currentBlock.Emit(new IrBeginExceptionBlock("", withStmt.Span));

        // Lower body
        LowerBlock(withStmt.Body);

        // Finally: dispose all context locals
        _currentBlock!.Emit(new IrBeginFinallyBlock(withStmt.Span));
        foreach (var ctxLocal in contextLocals)
        {
            _currentBlock.Emit(new IrLoadLocal(ctxLocal.Index, withStmt.Span));
            _currentBlock.Emit(new IrCallVirtual("Dispose", 0, withStmt.Span));
        }

        _currentBlock.Emit(new IrEndExceptionBlock(withStmt.Span));
    }

    private void LowerTupleUnpacking(TupleExpr tupleTarget, AssignmentStatement assign)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // RHS was already lowered by LowerAssignment before calling us.
        // TupleExpr RHS now produces an object[] via IrNewTuple, same as any other expression.
        // Store the tuple to a temp local, then access elements by index.

        // Determine element types from the RHS tuple type (if available)
        var rhsType = _typeChecker.ResolvedTypes.TryGetValue(assign.Value, out var rt) ? rt : null;
        var tupleElemTypes = rhsType is TupleCulebralType tct ? tct.Elements : null;

        var tupleLocal = CreateLocal("<unpack_tuple>", PrimitiveType.Object);
        _currentBlock.Emit(new IrStoreLocal(tupleLocal.Index, assign.Span));

        // Check for starred unpacking: a, *rest, b = items
        var starredIndex = -1;
        for (int si = 0; si < tupleTarget.Elements.Count; si++)
            if (tupleTarget.Elements[si] is StarredExpr) { starredIndex = si; break; }

        if (starredIndex >= 0)
        {
            // Convert source to object[] for uniform indexing.
            // Emit: Enumerable.ToArray(Enumerable.Cast<object>((IEnumerable)value))
            // This handles both List<object> and object[] inputs.
            _currentBlock.Emit(new IrLoadLocal(tupleLocal.Index, assign.Span));
            _currentBlock.Emit(new IrCallDotNetGenericStatic(
                typeof(Enumerable), "Cast", 1, [typeof(object)], assign.Span));
            _currentBlock.Emit(new IrCallDotNetGenericStatic(
                typeof(Enumerable), "ToArray", 1, [typeof(object)], assign.Span));
            _currentBlock.Emit(new IrStoreLocal(tupleLocal.Index, assign.Span));

            // Starred unpacking: head elements by index, starred gets slice, tail from end
            var headCount = starredIndex;
            var tailCount = tupleTarget.Elements.Count - starredIndex - 1;

            // Head elements: items[0], items[1], ...
            for (int hi = 0; hi < headCount; hi++)
            {
                _currentBlock.Emit(new IrLoadLocal(tupleLocal.Index, assign.Span));
                _currentBlock.Emit(new IrTupleElement(hi, assign.Span));
                if (tupleTarget.Elements[hi] is IdentifierExpr headId)
                {
                    var local = GetOrCreateLocal(headId.Name, assign.Span);
                    _currentBlock.Emit(new IrStoreLocal(local.Index, assign.Span));
                }
                else _currentBlock.Emit(new IrPop(assign.Span));
            }

            // Starred element: items[headCount .. len-tailCount]
            // This is a List<object> created by GetRange or similar
            // For now, store as a list using IrSlice or manual construction
            var starred = (StarredExpr)tupleTarget.Elements[starredIndex];
            if (starred.Operand is IdentifierExpr starId)
            {
                // Emit: new List<object>()
                _currentBlock.Emit(new IrNewObj("System.Collections.Generic.List`1", 0, assign.Span));
                var starListLocal = GetOrCreateLocal(starId.Name, assign.Span);
                _currentBlock.Emit(new IrStoreLocal(starListLocal.Index, assign.Span));

                // Add elements from headCount to len-tailCount
                // Get array length via System.Array.Length property
                var lenLocal = CreateLocal("<star_len>", PrimitiveType.Int);
                _currentBlock.Emit(new IrLoadLocal(tupleLocal.Index, assign.Span));
                _currentBlock.Emit(new IrCallDotNetInstance(typeof(Array), "get_Length", 0, assign.Span));
                _currentBlock.Emit(new IrStoreLocal(lenLocal.Index, assign.Span));

                // for (idx = headCount; idx < len - tailCount; idx++) starList.Add(tuple[idx])
                var idxLocal = CreateLocal("<star_idx>", PrimitiveType.Int);
                _currentBlock.Emit(new IrLoadInt(headCount, assign.Span));
                _currentBlock.Emit(new IrStoreLocal(idxLocal.Index, assign.Span));

                var starCond = NewBlockLabel("star_cond");
                var starBody = NewBlockLabel("star_body");
                var starEnd = NewBlockLabel("star_end");
                _currentBlock.Emit(new IrBranch(starCond, assign.Span));

                var starCondBlock = new IrBasicBlock { Label = starCond };
                _currentFunction.Body.Add(starCondBlock);
                _currentBlock = starCondBlock;
                _currentBlock.Emit(new IrLoadLocal(idxLocal.Index, assign.Span));
                _currentBlock.Emit(new IrLoadLocal(lenLocal.Index, assign.Span));
                _currentBlock.Emit(new IrLoadInt(tailCount, assign.Span));
                _currentBlock.Emit(new IrBinaryOp(IrBinaryOpKind.Sub, PrimitiveType.Int, assign.Span));
                _currentBlock.Emit(new IrBinaryOp(IrBinaryOpKind.LessThan, null, assign.Span));
                _currentBlock.Emit(new IrBranchIf(starBody, starEnd, assign.Span));

                var starBodyBlock = new IrBasicBlock { Label = starBody };
                _currentFunction.Body.Add(starBodyBlock);
                _currentBlock = starBodyBlock;
                _currentBlock.Emit(new IrLoadLocal(starListLocal.Index, assign.Span));
                _currentBlock.Emit(new IrLoadLocal(tupleLocal.Index, assign.Span));
                _currentBlock.Emit(new IrLoadLocal(idxLocal.Index, assign.Span));
                _currentBlock.Emit(new IrLoadElement(assign.Span));
                _currentBlock.Emit(new IrCallVirtual("Add", 1, assign.Span));
                // Increment idx
                _currentBlock.Emit(new IrLoadLocal(idxLocal.Index, assign.Span));
                _currentBlock.Emit(new IrLoadInt(1, assign.Span));
                _currentBlock.Emit(new IrBinaryOp(IrBinaryOpKind.Add, PrimitiveType.Int, assign.Span));
                _currentBlock.Emit(new IrStoreLocal(idxLocal.Index, assign.Span));
                _currentBlock.Emit(new IrBranch(starCond, assign.Span));

                var starEndBlock = new IrBasicBlock { Label = starEnd };
                _currentFunction.Body.Add(starEndBlock);
                _currentBlock = starEndBlock;
            }

            // Tail elements: items[len-tailCount], items[len-tailCount+1], ...
            for (int ti = 0; ti < tailCount; ti++)
            {
                var targetIdx = starredIndex + 1 + ti;
                // Index from end: len - tailCount + ti
                _currentBlock.Emit(new IrLoadLocal(tupleLocal.Index, assign.Span));
                // We need dynamic indexing: compute len - tailCount + ti
                _currentBlock.Emit(new IrLoadLocal(tupleLocal.Index, assign.Span));
                _currentBlock.Emit(new IrCallDotNetInstance(typeof(Array), "get_Length", 0, assign.Span));
                _currentBlock.Emit(new IrLoadInt(tailCount - ti, assign.Span));
                _currentBlock.Emit(new IrBinaryOp(IrBinaryOpKind.Sub, PrimitiveType.Int, assign.Span));
                _currentBlock.Emit(new IrLoadElement(assign.Span));
                if (tupleTarget.Elements[targetIdx] is IdentifierExpr tailId)
                {
                    var local = GetOrCreateLocal(tailId.Name, assign.Span);
                    _currentBlock.Emit(new IrStoreLocal(local.Index, assign.Span));
                }
                else _currentBlock.Emit(new IrPop(assign.Span));
            }
        }
        else
        {
            // Standard fixed-count unpacking (existing logic)
            for (int i = 0; i < tupleTarget.Elements.Count; i++)
            {
                _currentBlock.Emit(new IrLoadLocal(tupleLocal.Index, assign.Span));
                _currentBlock.Emit(new IrTupleElement(i, assign.Span));

                // Determine the element type for proper local typing
                var elemType = (tupleElemTypes is not null && i < tupleElemTypes.Length)
                    ? tupleElemTypes[i]
                    : (CulebralType)PrimitiveType.Object;

                if (tupleTarget.Elements[i] is IdentifierExpr targetIdent)
                {
                    var local = GetOrCreateLocal(targetIdent.Name, assign.Span, elemType);
                    // Unbox value types since tuple elements are stored as object in the array
                    if (elemType is PrimitiveType upt && upt.ClrType is not null && upt.ClrType.IsValueType)
                        _currentBlock.Emit(new IrUnbox(upt, assign.Span));
                    _currentBlock.Emit(new IrStoreLocal(local.Index, assign.Span));
                }
                else
                {
                    _currentBlock.Emit(new IrPop(assign.Span));
                }
            }
        }
    }

    // ─── is / in / try-except / raise / match / lambda / comprehensions ───

    private void LowerIsExpr(IsExpr isExpr)
    {
        if (_currentBlock is null) return;
        LowerExpression(isExpr.Left);

        // is None / is not None
        if (isExpr.Type is SimpleType { Name: "None" })
        {
            _currentBlock.Emit(new IrIsNull(isExpr.Negated, isExpr.Span));
        }
        else
        {
            // is Type / is not Type — type check via isinst
            var typeName = isExpr.Type is SimpleType st ? st.Name : "object";
            _currentBlock.Emit(new IrIsInst(typeName, isExpr.Span));
            if (isExpr.Negated)
            {
                // Negate the result
                _currentBlock.Emit(new IrUnaryOp(IrUnaryOpKind.LogicalNot, isExpr.Span));
            }
        }
    }

    private void LowerInExpr(InExpr inExpr)
    {
        if (_currentBlock is null) return;
        // x in collection → collection.Contains(x)
        // Reorder: evaluate collection first (for callvirt), then x
        LowerExpression(inExpr.Right); // collection
        LowerExpression(inExpr.Left);  // element

        // Check if collection is a user type with __contains__ → call Contains (our emitted alias)
        var collType = ResolveExpressionType(inExpr.Right);
        var collTypeName = collType?.DisplayName;
        if (collType == PrimitiveType.Str)
        {
            // String substring check: "bc" in "abcd" → "abcd".Contains("bc")
            _currentBlock.Emit(new IrCallDotNetInstance(typeof(string), "Contains", 1, inExpr.Span));
        }
        else if (collTypeName is not null && _typeDefs.TryGetValue(collTypeName, out var collTypeDef) &&
            collTypeDef.Methods.Any(m => m.Name == "__contains__"))
        {
            _currentBlock.Emit(new IrCallMethod(collTypeName, "Contains", 1, inExpr.Span));
        }
        else
        {
            _currentBlock.Emit(new IrCallVirtual("Contains", 1, inExpr.Span));
        }

        if (inExpr.Negated)
            _currentBlock.Emit(new IrUnaryOp(IrUnaryOpKind.LogicalNot, inExpr.Span));
    }

    private void LowerAssert(AssertStatement assertStmt)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Desugar: assert condition, message  →  if not condition: raise Exception(message)
        var passLabel = NewBlockLabel("assert_pass");
        var failLabel = NewBlockLabel("assert_fail");

        LowerExpression(assertStmt.Condition);
        _currentBlock.Emit(new IrBranchIf(passLabel, failLabel, assertStmt.Span));

        // Fail block: construct and throw exception
        var failBlock = new IrBasicBlock { Label = failLabel };
        _currentFunction.Body.Add(failBlock);
        _currentBlock = failBlock;

        if (assertStmt.Message is not null)
        {
            LowerExpression(assertStmt.Message);
            // new Exception(message)
            _currentBlock.Emit(new IrNewDotNetObj(typeof(Exception), 1, assertStmt.Span));
        }
        else
        {
            _currentBlock.Emit(new IrLoadString("Assertion failed", assertStmt.Span));
            _currentBlock.Emit(new IrNewDotNetObj(typeof(Exception), 1, assertStmt.Span));
        }
        _currentBlock.Emit(new IrThrow(assertStmt.Span));

        // Pass block: continue execution
        var passBlock = new IrBasicBlock { Label = passLabel };
        _currentFunction.Body.Add(passBlock);
        _currentBlock = passBlock;
    }

    private void LowerTryStatement(TryStatement tryStmt)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Emit all try/catch/finally instructions into the SAME basic block
        // This is required because CIL exception handling must be contiguous
        _currentBlock.Emit(new IrBeginExceptionBlock("", tryStmt.Span));

        // Try body
        foreach (var stmt in tryStmt.Body.Statements)
            LowerStatement(stmt);

        // Catch clauses
        foreach (var except in tryStmt.ExceptClauses)
        {
            var excType = typeof(Exception);
            if (except.ExceptionType is SimpleType st)
            {
                var resolved = _typeChecker.DotNetResolver.ResolveType($"System.{st.Name}")
                    ?? _typeChecker.DotNetResolver.ResolveType(st.Name);
                if (resolved is not null)
                    excType = resolved;
            }

            _currentBlock!.Emit(new IrBeginCatchBlock(excType, except.Span));

            if (except.Variable is not null)
            {
                var local = GetOrCreateLocal(except.Variable, except.Span, PrimitiveType.Object);
                _currentBlock.Emit(new IrStoreLocal(local.Index, except.Span));
            }
            else
            {
                _currentBlock.Emit(new IrPop(except.Span));
            }

            foreach (var stmt in except.Body.Statements)
                LowerStatement(stmt);
        }

        // Finally
        if (tryStmt.FinallyBody is not null)
        {
            _currentBlock!.Emit(new IrBeginFinallyBlock(tryStmt.Span));
            foreach (var stmt in tryStmt.FinallyBody.Statements)
                LowerStatement(stmt);
        }

        _currentBlock!.Emit(new IrEndExceptionBlock(tryStmt.Span));
    }

    private void LowerMatchStatement(MatchStatement matchStmt)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        var endLabel = NewBlockLabel("match_end");
        var subjectLocal = CreateLocal("<match_subject>", PrimitiveType.Object);

        // Evaluate subject once and store
        LowerExpression(matchStmt.Subject);
        // Box if value type for pattern matching
        var subjectType = _typeChecker.ResolvedTypes.TryGetValue(matchStmt.Subject, out var st2) ? st2 : null;
        if (subjectType is PrimitiveType { ClrType.IsValueType: true })
            _currentBlock.Emit(new IrBox(subjectType, matchStmt.Span));
        _currentBlock.Emit(new IrStoreLocal(subjectLocal.Index, matchStmt.Span));

        for (int i = 0; i < matchStmt.Cases.Count; i++)
        {
            var matchCase = matchStmt.Cases[i];
            var caseBodyLabel = NewBlockLabel($"case_body_{i}");
            var nextCaseLabel = i + 1 < matchStmt.Cases.Count
                ? NewBlockLabel($"case_test_{i + 1}")
                : endLabel;

            // Emit pattern test
            switch (matchCase.Pattern)
            {
                case WildcardPattern:
                    // Always matches — jump to body
                    _currentBlock.Emit(new IrBranch(caseBodyLabel, matchCase.Span));
                    break;

                case LiteralPattern litPat:
                    _currentBlock.Emit(new IrLoadLocal(subjectLocal.Index, matchCase.Span));
                    // Unbox the subject to match the literal type
                    if (litPat.Literal is IntLiteralExpr)
                        _currentBlock.Emit(new IrUnbox(PrimitiveType.Int, matchCase.Span));
                    else if (litPat.Literal is BoolLiteralExpr)
                        _currentBlock.Emit(new IrUnbox(PrimitiveType.Bool, matchCase.Span));
                    LowerExpression(litPat.Literal);
                    _currentBlock.Emit(new IrBinaryOp(IrBinaryOpKind.Equal, null, matchCase.Span));
                    _currentBlock.Emit(new IrBranchIf(caseBodyLabel, nextCaseLabel, matchCase.Span));
                    break;

                case NamePattern namePat:
                    // Bind the subject to the name
                    var nameLocal = GetOrCreateLocal(namePat.Name, matchCase.Span);
                    _currentBlock.Emit(new IrLoadLocal(subjectLocal.Index, matchCase.Span));
                    _currentBlock.Emit(new IrStoreLocal(nameLocal.Index, matchCase.Span));
                    _currentBlock.Emit(new IrBranch(caseBodyLabel, matchCase.Span));
                    break;

                case NonePattern:
                    _currentBlock.Emit(new IrLoadLocal(subjectLocal.Index, matchCase.Span));
                    _currentBlock.Emit(new IrIsNull(false, matchCase.Span));
                    _currentBlock.Emit(new IrBranchIf(caseBodyLabel, nextCaseLabel, matchCase.Span));
                    break;

                case OrPattern orPat:
                {
                    // Try each alternative: if any matches, go to body
                    for (int ai = 0; ai < orPat.Alternatives.Count; ai++)
                    {
                        var alt = orPat.Alternatives[ai];
                        var nextAltLabel = ai < orPat.Alternatives.Count - 1
                            ? NewBlockLabel($"case_{i}_or_{ai + 1}")
                            : nextCaseLabel;

                        if (alt is LiteralPattern altLit)
                        {
                            _currentBlock!.Emit(new IrLoadLocal(subjectLocal.Index, matchCase.Span));
                            if (altLit.Literal is IntLiteralExpr)
                                _currentBlock.Emit(new IrUnbox(PrimitiveType.Int, matchCase.Span));
                            LowerExpression(altLit.Literal);
                            _currentBlock.Emit(new IrBinaryOp(IrBinaryOpKind.Equal, null, matchCase.Span));
                            _currentBlock.Emit(new IrBranchIf(caseBodyLabel, nextAltLabel, matchCase.Span));
                        }
                        else if (alt is NonePattern)
                        {
                            _currentBlock!.Emit(new IrLoadLocal(subjectLocal.Index, matchCase.Span));
                            _currentBlock.Emit(new IrIsNull(false, matchCase.Span));
                            _currentBlock.Emit(new IrBranchIf(caseBodyLabel, nextAltLabel, matchCase.Span));
                        }
                        else if (alt is WildcardPattern)
                        {
                            _currentBlock!.Emit(new IrBranch(caseBodyLabel, matchCase.Span));
                        }
                        else
                        {
                            _currentBlock!.Emit(new IrBranch(nextAltLabel, matchCase.Span));
                        }

                        if (ai < orPat.Alternatives.Count - 1)
                        {
                            var nextAltBlock = new IrBasicBlock { Label = nextAltLabel };
                            _currentFunction!.Body.Add(nextAltBlock);
                            _currentBlock = nextAltBlock;
                        }
                    }
                    break;
                }

                default:
                    // Unsupported pattern — skip to next
                    _currentBlock.Emit(new IrBranch(nextCaseLabel, matchCase.Span));
                    break;
            }

            // Guard
            if (matchCase.Guard is not null)
            {
                // Body label becomes the guard check
                var guardedBodyLabel = NewBlockLabel($"case_guarded_{i}");
                var guardBlock = new IrBasicBlock { Label = caseBodyLabel };
                _currentFunction.Body.Add(guardBlock);
                _currentBlock = guardBlock;

                LowerExpression(matchCase.Guard);
                _currentBlock.Emit(new IrBranchIf(guardedBodyLabel, nextCaseLabel, matchCase.Span));

                var guardedBlock = new IrBasicBlock { Label = guardedBodyLabel };
                _currentFunction.Body.Add(guardedBlock);
                _currentBlock = guardedBlock;
            }
            else
            {
                var bodyBlock = new IrBasicBlock { Label = caseBodyLabel };
                _currentFunction.Body.Add(bodyBlock);
                _currentBlock = bodyBlock;
            }

            LowerBlock(matchCase.Body);
            _currentBlock?.Emit(new IrBranch(endLabel, matchCase.Span));

            // Next case test block
            if (i + 1 < matchStmt.Cases.Count)
            {
                var nextBlock = new IrBasicBlock { Label = nextCaseLabel };
                _currentFunction.Body.Add(nextBlock);
                _currentBlock = nextBlock;
            }
        }

        var end = new IrBasicBlock { Label = endLabel };
        _currentFunction.Body.Add(end);
        _currentBlock = end;
    }

    /// <summary>
    /// Infer generic type arguments for an extension method from the receiver type.
    /// E.g., for Enumerable.Count&lt;TSource&gt;(IEnumerable&lt;TSource&gt;), if the receiver is
    /// List&lt;object&gt;, the type arg is object.
    /// </summary>
    private static Type[] InferExtensionTypeArgs(System.Reflection.MethodInfo genericMethod, Type receiverType)
    {
        var genArgs = genericMethod.GetGenericArguments();
        var firstParamType = genericMethod.GetParameters()[0].ParameterType;
        var inferred = new Type[genArgs.Length];

        // Default all to object
        for (int i = 0; i < inferred.Length; i++)
            inferred[i] = typeof(object);

        // Try to infer from the receiver type's generic interface implementations
        if (firstParamType.IsGenericType)
        {
            var genericDef = firstParamType.GetGenericTypeDefinition();

            // Find the matching interface on the receiver
            Type? matchingInterface = null;
            if (receiverType.IsGenericType && receiverType.GetGenericTypeDefinition() == genericDef)
                matchingInterface = receiverType;
            else
                matchingInterface = receiverType.GetInterfaces()
                    .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericDef);

            if (matchingInterface is not null)
            {
                var interfaceArgs = matchingInterface.GetGenericArguments();
                var paramArgs = firstParamType.GetGenericArguments();

                for (int i = 0; i < paramArgs.Length; i++)
                {
                    if (paramArgs[i].IsGenericParameter)
                    {
                        var pos = paramArgs[i].GenericParameterPosition;
                        if (pos < inferred.Length && i < interfaceArgs.Length)
                            inferred[pos] = interfaceArgs[i];
                    }
                }
            }
        }

        return inferred;
    }

    private void LowerLambdaExpr(LambdaExpr lambda)
    {
        if (_currentBlock is null || _currentFunction is null || _module is null) return;

        var lambdaName = $"<lambda>_{_lambdaCounter++}";

        // Create IR parameters for the lambda (all object-typed for simplicity)
        var parameters = lambda.Parameters.Select((p, i) => new IrParameter
        {
            Name = p.Name,
            Type = PrimitiveType.Object,
            Index = i,
        }).ToList();

        // Build the lambda body: evaluate the body expression and return it
        var entryBlock = new IrBasicBlock { Label = NewBlockLabel("lambda_entry") };
        var body = new List<IrBasicBlock> { entryBlock };

        // Infer the lambda body's return type for correct delegate signature
        // This is critical for .NET interop — ASP.NET inspects delegate return types
        var bodyType = _typeChecker.ResolvedTypes.TryGetValue(lambda.Body, out var bt) ? bt : PrimitiveType.Object;
        var returnType = bodyType ?? PrimitiveType.Object;

        var irFunc = new IrFunction
        {
            Name = lambdaName,
            ReturnType = returnType,
            Parameters = parameters,
            Body = body,
            IsStatic = true,
        };

        // Save current lowering state
        var prevFunction = _currentFunction;
        var prevBlock = _currentBlock;
        var prevLocalCounter = _localCounter;
        var prevDeclaringType = _currentDeclaringType;

        _currentFunction = irFunc;
        _currentBlock = entryBlock;
        _localCounter = 0;
        _currentDeclaringType = null;

        // Lower the lambda body expression
        LowerExpression(lambda.Body);

        _currentBlock.Emit(new IrReturn(true, lambda.Span));

        // Restore lowering state
        _currentFunction = prevFunction;
        _currentBlock = prevBlock;
        _localCounter = prevLocalCounter;
        _currentDeclaringType = prevDeclaringType;

        // Add the lambda function to the module
        _module.Functions.Add(irFunc);

        // At the call site, emit a delegate creation instruction
        _currentBlock.Emit(new IrCreateDelegate(lambdaName, lambda.Parameters.Count, lambda.Span));
    }

    private void LowerSliceExpr(SliceExpr slice)
    {
        if (_currentBlock is null) return;

        // Push the source object
        LowerExpression(slice.Object);

        // Push start (or 0 if absent)
        if (slice.Lower is not null)
            LowerExpression(slice.Lower);
        else
            _currentBlock.Emit(new IrLoadInt(0, slice.Span));

        // Push stop (or -1 sentinel if absent, meaning "to end")
        if (slice.Upper is not null)
            LowerExpression(slice.Upper);
        else
            _currentBlock.Emit(new IrLoadInt(-1, slice.Span));

        // Push step (or 1 if absent)
        if (slice.Step is not null)
            LowerExpression(slice.Step);
        else
            _currentBlock.Emit(new IrLoadInt(1, slice.Span));

        _currentBlock.Emit(new IrSlice(slice.Lower is not null, slice.Upper is not null, slice.Step is not null, slice.Span));
    }

    private void LowerWithExpr(WithExpr withExpr)
    {
        if (_currentBlock is null || _currentFunction is null) return;

        // Get the type of the source expression
        var sourceType = _typeChecker.ResolvedTypes.TryGetValue(withExpr.Source, out var st) ? st : null;
        var typeName = sourceType?.DisplayName;

        if (typeName is null || !_typeDefs.TryGetValue(typeName, out var typeDef))
        {
            // Fallback: just lower the source (can't resolve type)
            LowerExpression(withExpr.Source);
            return;
        }

        // Lower the source expression and store to a temp local
        LowerExpression(withExpr.Source);
        var tempLocal = CreateLocal("<with_src>", sourceType!);
        _currentBlock.Emit(new IrStoreLocal(tempLocal.Index, withExpr.Span));

        // Build the update lookup for quick field name matching
        var updateMap = new Dictionary<string, Expression>();
        foreach (var (name, value) in withExpr.Updates)
            updateMap[name] = value;

        // For each field in the type definition (in order), push either the update value or the original field
        foreach (var field in typeDef.Fields)
        {
            if (updateMap.TryGetValue(field.Name, out var updateValue))
            {
                LowerExpression(updateValue);
            }
            else
            {
                // Load from the source object
                _currentBlock.Emit(new IrLoadLocal(tempLocal.Index, withExpr.Span));
                _currentBlock.Emit(new IrLoadField(typeName, field.Name, withExpr.Span));
            }
        }

        // Emit constructor call with all fields
        _currentBlock.Emit(new IrNewObj(typeName, typeDef.Fields.Count, withExpr.Span));
    }

    // ─── Helpers ───

    private IrLocal GetOrCreateLocal(string name, SourceSpan span, CulebralType? type = null)
    {
        if (_currentFunction is null)
            throw new InvalidOperationException("No current function");

        var existing = _currentFunction.Locals.FirstOrDefault(l => l.Name == name);
        if (existing is not null)
            return existing;

        return CreateLocal(name, type ?? PrimitiveType.Object);
    }

    private IrLocal CreateLocal(string name, CulebralType type)
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

    /// <summary>
    /// Extract decorator metadata from AST Decorator nodes into IR IrDecorator objects,
    /// preserving both the name and any constant arguments for .NET attribute emission.
    /// </summary>
    private static List<IrDecorator>? ExtractDecorators(List<Decorator> decorators)
    {
        if (decorators.Count == 0)
            return null;

        var result = new List<IrDecorator>();
        foreach (var d in decorators)
        {
            var (name, args) = d.Expr switch
            {
                IdentifierExpr id => (id.Name, new List<object?>()),
                CallExpr { Callee: IdentifierExpr callId, Arguments: var callArgs } =>
                    (callId.Name, ExtractConstantArgs(callArgs)),
                MemberAccessExpr ma => ($"{ExtractExprName(ma.Object)}.{ma.Member}", new List<object?>()),
                CallExpr { Callee: MemberAccessExpr ma, Arguments: var callArgs } =>
                    ($"{ExtractExprName(ma.Object)}.{ma.Member}", ExtractConstantArgs(callArgs)),
                _ => ((string?)null, new List<object?>()),
            };

            if (name is not null)
                result.Add(new IrDecorator { Name = name, Arguments = args });
        }

        return result.Count > 0 ? result : null;
    }

    private static string ExtractExprName(Expression expr)
    {
        return expr switch
        {
            IdentifierExpr id => id.Name,
            MemberAccessExpr ma => $"{ExtractExprName(ma.Object)}.{ma.Member}",
            _ => "unknown",
        };
    }

    private static List<object?> ExtractConstantArgs(List<Argument> args)
    {
        var result = new List<object?>();
        foreach (var arg in args)
        {
            var value = arg.Value switch
            {
                StringLiteralExpr s => (object?)s.Value,
                IntLiteralExpr i => i.Value,
                FloatLiteralExpr f => f.Value,
                BoolLiteralExpr b => b.Value,
                NoneLiteralExpr => null,
                _ => null, // Non-constant args — emit as null (best effort)
            };
            result.Add(value);
        }
        return result;
    }

    private string NewBlockLabel(string prefix) => $"{prefix}_{_blockCounter++}";

    private static bool EndsWithReturn(IrBasicBlock block)
    {
        if (block.Instructions.Count == 0) return false;
        return block.Instructions[^1] is IrReturn;
    }

    private bool InstructionLeavesValue(IrInstruction instr)
    {
        return instr switch
        {
            IrPrint => false, // void
            IrCallBuiltin { Name: "print" } => false, // void (legacy, shouldn't occur)
            IrCallBuiltin { Name: "assert_equal" or "assert_not_equal" } => false, // void
            IrCallBuiltin => true,
            IrCall => true, // conservative — may be void but safer to pop
            IrCallMethod { DeclaringType: var dt, MethodName: var mn } =>
                !(_typeDefs.TryGetValue(dt, out var td)
                  && td.Methods.FirstOrDefault(m => m.Name == mn) is { ReturnType: var rt }
                  && rt == PrimitiveType.Void),
            IrCallVirtual { MethodName: "Add" } => false, // List.Add returns void
            IrCallVirtual => true,
            IrCallDotNetStatic { DeclaringType: var t, MethodName: var n, ArgCount: var a }
                => FindDotNetMethodReturnType(t, n, a, true) != typeof(void),
            IrCallDotNetInstance { DeclaringType: var t, MethodName: var n, ArgCount: var a }
                => FindDotNetMethodReturnType(t, n, a, false) != typeof(void),
            IrCallDotNetGenericStatic => true, // generic methods typically return values
            IrCallDotNetGenericInstance => true,
            IrCallExtensionMethod { DeclaringType: var et, MethodName: var en, ArgCount: var ea }
                => FindExtensionMethodReturnType(et, en, ea) != typeof(void),
            IrNewDotNetObj => true,
            IrNewObj => true,
            IrCreateDelegate => true,
            IrInvokeDelegate => true,
            IrSlice => true,
            IrNewArrayFromStack => true,
            IrNewTuple => true,
            IrTupleElement => true,
            IrAwait { HasResult: var hr } => hr,
            IrStoreLocal or IrStoreField or IrPop or IrNop or IrReturn or IrBranch or IrBranchIf => false,
            _ => false,
        };
    }

    private Type FindDotNetMethodReturnType(Type type, string name, int argCount, bool isStatic)
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.FlattenHierarchy |
                    (isStatic ? System.Reflection.BindingFlags.Static : System.Reflection.BindingFlags.Instance);
        var method = type.GetMethods(flags)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argCount && !m.IsGenericMethod);
        return method?.ReturnType ?? typeof(void);
    }

    private Type FindExtensionMethodReturnType(Type extensionSourceType, string name, int argCount)
    {
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
        // Extension methods have argCount+1 params (receiver + explicit args)
        var method = extensionSourceType.GetMethods(flags)
            .FirstOrDefault(m => m.Name == name
                && m.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false)
                && m.GetParameters().Length == argCount + 1);
        if (method is null) return typeof(void);
        // For generic methods, the return type may reference type parameters — return object as fallback
        return method.ReturnType.ContainsGenericParameters ? typeof(object) : method.ReturnType;
    }

    /// <summary>
    /// Resolves the type of an expression using type checker resolved types, local variables, and parameters.
    /// Useful when the type checker may not have visited a sub-expression (e.g. children of InExpr).
    /// </summary>
    private CulebralType? ResolveExpressionType(Expression expr)
    {
        // First try the type checker's resolved types
        if (_typeChecker.ResolvedTypes.TryGetValue(expr, out var resolved))
            return resolved;

        // For identifier expressions, look up in current function context
        if (expr is IdentifierExpr ident && _currentFunction is not null)
        {
            var local = _currentFunction.Locals.FirstOrDefault(l => l.Name == ident.Name);
            if (local is not null) return local.Type;

            var param = _currentFunction.Parameters.FirstOrDefault(p => p.Name == ident.Name);
            if (param is not null) return param.Type;
        }

        return null;
    }

    /// <summary>
    /// When the type checker resolves a member access like `other.x` as `object`,
    /// look up the actual field type from the known type definitions.
    /// Returns null if no refinement is possible.
    /// </summary>
    private CulebralType? RefineFieldAccessType(Expression expr)
    {
        if (expr is MemberAccessExpr member)
        {
            var objType = _typeChecker.ResolvedTypes.TryGetValue(member.Object, out var ot) ? ot : null;
            var typeName = objType?.DisplayName;
            if (typeName is not null)
            {
                // Try the completed type defs first, then fall back to the type currently being lowered
                IrTypeDef? typeDef = null;
                if (!_typeDefs.TryGetValue(typeName, out typeDef) &&
                    _currentTypeDef is not null && _currentTypeDef.Name == typeName)
                {
                    typeDef = _currentTypeDef;
                }

                if (typeDef is not null)
                {
                    var field = typeDef.Fields.FirstOrDefault(f => f.Name == member.Member);
                    if (field is not null && field.Type != PrimitiveType.Object)
                        return field.Type;
                }
            }
        }
        return null;
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

    /// <summary>
    /// Walks the type hierarchy to find a method. Returns (declaringTypeName, typeDef) or (null, null).
    /// </summary>
    private (string? TypeName, IrTypeDef? TypeDef) FindMethodInHierarchy(string? typeName, string? methodName)
    {
        if (typeName is null || methodName is null) return (null, null);
        var current = typeName;
        while (current is not null)
        {
            if (_typeDefs.TryGetValue(current, out var td) && td.Methods.Any(m => m.Name == methodName))
                return (current, td);
            // Walk to base type
            if (_typeDefs.TryGetValue(current, out var currentTd) && currentTd.BaseType is not null)
                current = currentTd.BaseType;
            else
                break;
        }
        return (null, null);
    }

    private static string? BinaryOpToDunder(IrBinaryOpKind op) => op switch
    {
        IrBinaryOpKind.Add => "__add__",
        IrBinaryOpKind.Sub => "__sub__",
        IrBinaryOpKind.Mul => "__mul__",
        IrBinaryOpKind.Div => "__truediv__",
        IrBinaryOpKind.IntDiv => "__floordiv__",
        IrBinaryOpKind.Mod => "__mod__",
        IrBinaryOpKind.Pow => "__pow__",
        IrBinaryOpKind.Equal => "__eq__",
        IrBinaryOpKind.NotEqual => "__ne__",
        IrBinaryOpKind.LessThan => "__lt__",
        IrBinaryOpKind.LessEqual => "__le__",
        IrBinaryOpKind.GreaterThan => "__gt__",
        IrBinaryOpKind.GreaterEqual => "__ge__",
        IrBinaryOpKind.BitAnd => "__and__",
        IrBinaryOpKind.BitOr => "__or__",
        IrBinaryOpKind.BitXor => "__xor__",
        IrBinaryOpKind.ShiftLeft => "__lshift__",
        IrBinaryOpKind.ShiftRight => "__rshift__",
        _ => null,
    };

    private static string? UnaryOpToDunder(TokenKind op) => op switch
    {
        TokenKind.Minus => "__neg__",
        TokenKind.Tilde => "__invert__",
        _ => null,
    };

    // ─── .NET Import Processing ───

    private void ProcessFromImport(FromImportStatement fromImport)
    {
        var resolver = _typeChecker.DotNetResolver;
        foreach (var name in fromImport.Names)
        {
            var fullTypeName = $"{fromImport.ModulePath}.{name.Name}";
            var clrType = resolver.ResolveType(fullTypeName);
            if (clrType is not null)
            {
                var symbolName = name.Alias ?? name.Name;
                _importedDotNetTypes[symbolName] = clrType;

                // Track types that contain extension methods for later resolution
                if (HasExtensionMethods(clrType))
                    _extensionMethodSources.Add(clrType);
            }
        }

        // Scan the imported namespace for ALL extension method types.
        // This enables C#-style extension method resolution: when you import from a namespace,
        // all extension methods defined in that namespace become available on compatible receivers.
        // E.g., `from Microsoft.AspNetCore.Builder import WebApplication` also makes
        // EndpointRouteBuilderExtensions.MapGet available as app.map_get(...)
        ScanNamespaceForExtensionMethods(fromImport.ModulePath);
    }

    /// <summary>
    /// Scan all loaded assemblies for types in the given namespace that define extension methods,
    /// and register them as extension method sources for instance-style resolution.
    /// </summary>
    private void ScanNamespaceForExtensionMethods(string namespaceName)
    {
        if (!_scannedExtensionNamespaces.Add(namespaceName))
            return; // Already scanned

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                // Some assemblies may have types that can't be loaded — use what we can
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.Namespace != namespaceName)
                    continue;
                if (!HasExtensionMethods(type))
                    continue;
                if (_extensionMethodSources.Contains(type))
                    continue;
                _extensionMethodSources.Add(type);
            }
        }
    }

    /// <summary>Check if a type defines any extension methods.</summary>
    private static bool HasExtensionMethods(Type type)
    {
        return type.IsAbstract && type.IsSealed // static class
            && type.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false);
    }

    private void ProcessImport(ImportStatement import)
    {
        var resolver = _typeChecker.DotNetResolver;
        var clrType = resolver.ResolveType(import.ModulePath);
        if (clrType is not null)
        {
            var symbolName = import.Alias ?? clrType.Name;
            _importedDotNetTypes[symbolName] = clrType;
        }
        else
        {
            var symbolName = import.Alias ?? import.ModulePath.Split('.')[^1];
            _namespaceAliases[symbolName] = import.ModulePath;
        }
    }

    /// <summary>Check if a name refers to an imported .NET type.</summary>
    private Type? ResolveDotNetType(string name)
    {
        if (_importedDotNetTypes.TryGetValue(name, out var t))
            return t;
        return null;
    }

    /// <summary>Resolve a dotted chain like io.File to a .NET type.</summary>
    private Type? ResolveDotNetChain(string namespaceName, string typeName)
    {
        string ns;
        if (_namespaceAliases.TryGetValue(namespaceName, out var resolvedNs))
            ns = resolvedNs;
        else
            ns = namespaceName;

        return _typeChecker.DotNetResolver.ResolveType($"{ns}.{typeName}");
    }

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

    /// <summary>
    /// Infers the type of an expression from the type checker's resolved types,
    /// or by examining the expression structure (locals, parameters, literals).
    /// </summary>
    private CulebralType? InferExpressionType(Expression expr)
    {
        // First try the type checker's resolved types
        if (_typeChecker.ResolvedTypes.TryGetValue(expr, out var resolved))
            return resolved;

        // Fall back to structural inference
        return expr switch
        {
            IntLiteralExpr => PrimitiveType.Int,
            FloatLiteralExpr => PrimitiveType.Float,
            BoolLiteralExpr => PrimitiveType.Bool,
            StringLiteralExpr => PrimitiveType.Str,
            NoneLiteralExpr => PrimitiveType.Object,
            IdentifierExpr ident when _currentFunction is not null =>
                _currentFunction.Locals.FirstOrDefault(l => l.Name == ident.Name)?.Type
                ?? _currentFunction.Parameters.FirstOrDefault(p => p.Name == ident.Name)?.Type,
            BinaryExpr bin => InferBinaryExprType(bin),
            _ => null,
        };
    }

    private CulebralType? InferBinaryExprType(BinaryExpr bin)
    {
        // Arithmetic/comparison ops on ints produce int
        var leftType = InferExpressionType(bin.Left);
        if (leftType is PrimitiveType pt)
            return pt;
        return InferExpressionType(bin.Right);
    }

    /// <summary>
    /// Emits a truthiness conversion (IrCallBuiltin("bool", 1)) if the given expression's
    /// resolved type is not already bool. This enables using non-bool values in boolean contexts
    /// (if, while, and, or, not).
    /// </summary>
    private void EmitTruthinessIfNeeded(Expression condition)
    {
        if (_currentBlock is null) return;
        var condType = _typeChecker.ResolvedTypes.TryGetValue(condition, out var ct) ? ct : null;
        if (condType is not null && condType != PrimitiveType.Bool)
        {
            _currentBlock.Emit(new IrCallBuiltin("bool", 1, condition.Span));
        }
    }
}
