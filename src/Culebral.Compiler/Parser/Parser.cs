using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Lexer;

namespace Culebral.Compiler.Parser;

/// <summary>
/// Recursive descent parser for Culebral.
/// Consumes tokens from the lexer and produces an AST.
/// Handles indentation-based blocks via INDENT/DEDENT tokens.
/// </summary>
public sealed class CulebralParser
{
    private readonly List<Token> _tokens;
    private readonly DiagnosticBag _diagnostics;
    private int _pos;
    private bool _inClassBody; // true when parsing methods inside a class/struct/record/interface

    public CulebralParser(List<Token> tokens, DiagnosticBag diagnostics)
    {
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public CompilationUnit ParseCompilationUnit()
    {
        var statements = new List<AstNode>();
        SkipNewlines();

        while (!IsAtEnd)
        {
            var stmt = ParseStatement();
            if (stmt is not null)
                statements.Add(stmt);
            SkipNewlines();
        }

        var span = statements.Count > 0
            ? new SourceSpan(statements[0].Span.Start, statements[^1].Span.End)
            : SourceSpan.None;

        return new CompilationUnit(statements, span);
    }

    // ─── Statement Parsing ───

    private Statement? ParseStatement()
    {
        SkipNewlines();
        if (IsAtEnd) return null;

        return Current.Kind switch
        {
            TokenKind.KwDef => ParseFunctionDef(isAsync: false),
            TokenKind.KwAsync => ParseAsyncDef(),
            TokenKind.KwClass => ParseClassDef(),
            TokenKind.KwStruct => ParseStructDef(),
            TokenKind.KwRecord => ParseRecordDef(),
            TokenKind.KwEnum => ParseEnumDef(),
            TokenKind.KwInterface => ParseInterfaceDef(),
            TokenKind.KwIf => ParseIfStatement(),
            TokenKind.KwWhile => ParseWhileStatement(),
            TokenKind.KwFor => ParseForStatement(),
            TokenKind.KwReturn => ParseReturnStatement(),
            TokenKind.KwYield => ParseYieldStatement(),
            TokenKind.KwBreak => ParseBreakStatement(),
            TokenKind.KwContinue => ParseContinueStatement(),
            TokenKind.KwPass => ParsePassStatement(),
            TokenKind.KwImport => ParseImportStatement(),
            TokenKind.KwFrom => ParseFromImportStatement(),
            TokenKind.KwWith => ParseWithStatement(),
            TokenKind.KwMatch => ParseMatchStatement(),
            TokenKind.KwWhen => ParseWhenStatement(),
            TokenKind.KwTry => ParseTryStatement(),
            TokenKind.KwRaise => ParseRaiseStatement(),
            TokenKind.KwAssert => ParseAssertStatement(),
            TokenKind.KwType when Peek(1).Kind == TokenKind.Identifier => ParseTypeAlias(),
            TokenKind.KwType => ParseExpressionOrAssignment(),
            TokenKind.At => ParseDecoratedDef(),
            TokenKind.AtIdentifier when IsDecoratorContext() => ParseDecoratedDef(),
            _ => ParseExpressionOrAssignment(),
        };
    }

    // ─── Function Definition ───

    private FunctionDef ParseFunctionDef(bool isAsync, List<Decorator>? decorators = null)
    {
        var start = Current.Span.Start;
        Expect(TokenKind.KwDef);
        var name = Expect(TokenKind.Identifier).Lexeme;

        // Optional type parameters
        // Not parsing them at function level yet for simplicity

        Expect(TokenKind.LeftParen);
        var parameters = ParseParameterList();
        Expect(TokenKind.RightParen);

        TypeAnnotation? returnType = null;
        if (TryConsume(TokenKind.Arrow))
            returnType = ParseTypeAnnotation();

        // Abstract/interface methods may have no body (no colon)
        if (Current.Kind == TokenKind.Colon)
        {
            Advance();
            var body = ParseBlock();
            return new FunctionDef(
                name, parameters, returnType, body, isAsync,
                decorators ?? [], new SourceSpan(start, body.Span.End));
        }

        // No body — abstract method declaration
        var emptyBody = new Block([], SourceSpan.From(CurrentLocation()));
        return new FunctionDef(
            name, parameters, returnType, emptyBody, isAsync,
            decorators ?? [], new SourceSpan(start, CurrentLocation()));
    }

    private Statement ParseAsyncDef()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'async'

        // async for — desugars to async enumeration with await MoveNextAsync
        if (Current.Kind == TokenKind.KwFor)
            return ParseForStatement(isAsync: true);

        // async with — desugars to async disposal with await DisposeAsync
        if (Current.Kind == TokenKind.KwWith)
            return ParseWithStatement(isAsync: true);

        if (Current.Kind != TokenKind.KwDef)
        {
            _diagnostics.Error("LEB1001", "Expected 'def', 'for', or 'with' after 'async'", Current.Span);
            return new PassStatement(SourceSpan.From(start));
        }
        return ParseFunctionDef(isAsync: true);
    }

    private List<Parameter> ParseParameterList()
    {
        var parameters = new List<Parameter>();
        if (Current.Kind == TokenKind.RightParen)
            return parameters;

        var first = ParseParameter();
        // Silently skip 'self' as first parameter (Python compatibility)
        // self with no type annotation → not a real parameter
        if (first.Name == "self" && first.Type is SimpleType { Name: "object" } && _inClassBody)
        {
            // Skip self from method signature — only in class methods
        }
        else
        {
            parameters.Add(first);
        }

        while (TryConsume(TokenKind.Comma))
        {
            if (Current.Kind == TokenKind.RightParen)
                break; // trailing comma
            parameters.Add(ParseParameter());
        }

        return parameters;
    }

    private Parameter ParseParameter()
    {
        var start = Current.Span.Start;
        var isVarArgs = false;

        if (TryConsume(TokenKind.Star))
            isVarArgs = true;

        var name = Expect(TokenKind.Identifier).Lexeme;
        TypeAnnotation type;

        if (TryConsume(TokenKind.Colon))
        {
            type = ParseTypeAnnotation();
        }
        else if (name == "self" && _inClassBody)
        {
            // 'self' is allowed without a type annotation only in class methods
            type = new SimpleType("object", SourceSpan.From(start));
        }
        else
        {
            _diagnostics.Error("LEB1008", $"Parameter '{name}' requires a type annotation (e.g., {name}: int)", new SourceSpan(start, CurrentLocation()));
            type = new SimpleType("object", SourceSpan.From(start)); // recover gracefully
        }

        Expression? defaultValue = null;
        if (TryConsume(TokenKind.Assign))
            defaultValue = ParseExpression();

        return new Parameter(name, type, defaultValue, isVarArgs, new SourceSpan(start, CurrentLocation()));
    }

    // ─── Class Definition ───

    private ClassDef ParseClassDef(List<Decorator>? decorators = null)
    {
        var start = Current.Span.Start;
        Advance(); // consume 'class'
        var name = Expect(TokenKind.Identifier).Lexeme;

        var typeParams = TryParseTypeParameters();

        var bases = new List<TypeAnnotation>();
        if (TryConsume(TokenKind.LeftParen))
        {
            if (Current.Kind != TokenKind.RightParen)
            {
                bases.Add(ParseTypeAnnotation());
                while (TryConsume(TokenKind.Comma))
                    bases.Add(ParseTypeAnnotation());
            }
            Expect(TokenKind.RightParen);
        }

        Expect(TokenKind.Colon);
        var members = ParseClassBody();
        var end = members.Count > 0 ? members[^1].Span.End : CurrentLocation();

        return new ClassDef(name, typeParams, bases, members, decorators ?? [], new SourceSpan(start, end));
    }

    private StructDef ParseStructDef()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'struct'
        var name = Expect(TokenKind.Identifier).Lexeme;
        var typeParams = TryParseTypeParameters();
        Expect(TokenKind.Colon);
        var members = ParseClassBody();
        var end = members.Count > 0 ? members[^1].Span.End : CurrentLocation();
        return new StructDef(name, typeParams, members, new SourceSpan(start, end));
    }

    private RecordDef ParseRecordDef()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'record'
        var name = Expect(TokenKind.Identifier).Lexeme;
        var typeParams = TryParseTypeParameters();
        Expect(TokenKind.Colon);
        var members = ParseClassBody();
        var end = members.Count > 0 ? members[^1].Span.End : CurrentLocation();
        return new RecordDef(name, typeParams, members, new SourceSpan(start, end));
    }

    private EnumDef ParseEnumDef()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'enum'
        var name = Expect(TokenKind.Identifier).Lexeme;
        Expect(TokenKind.Colon);

        var variants = new List<EnumVariant>();
        ExpectNewlineAndIndent();

        while (!IsAtEnd && Current.Kind != TokenKind.Dedent)
        {
            SkipNewlines();
            if (Current.Kind == TokenKind.Dedent) break;

            var varStart = Current.Span.Start;
            var varName = Expect(TokenKind.Identifier).Lexeme;

            List<Parameter>? fields = null;
            if (TryConsume(TokenKind.LeftParen))
            {
                fields = ParseParameterList();
                Expect(TokenKind.RightParen);
            }

            variants.Add(new EnumVariant(varName, fields, new SourceSpan(varStart, CurrentLocation())));
            SkipNewlines();
        }

        if (Current.Kind == TokenKind.Dedent)
            Advance();

        return new EnumDef(name, variants, new SourceSpan(start, CurrentLocation()));
    }

    private InterfaceDef ParseInterfaceDef()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'interface'
        var name = Expect(TokenKind.Identifier).Lexeme;
        var typeParams = TryParseTypeParameters();
        Expect(TokenKind.Colon);
        var members = ParseClassBody();
        var end = members.Count > 0 ? members[^1].Span.End : CurrentLocation();
        return new InterfaceDef(name, typeParams, members, new SourceSpan(start, end));
    }

    private List<AstNode> ParseClassBody()
    {
        var prev = _inClassBody;
        _inClassBody = true;
        var members = new List<AstNode>();
        ExpectNewlineAndIndent();

        while (!IsAtEnd && Current.Kind != TokenKind.Dedent)
        {
            SkipNewlines();
            if (Current.Kind == TokenKind.Dedent) break;

            var member = Current.Kind switch
            {
                TokenKind.KwDef => (AstNode)ParseFunctionDef(isAsync: false),
                TokenKind.KwAsync => ParseAsyncDef(),
                TokenKind.KwProp => ParsePropertyDef(),
                TokenKind.KwPass => ParsePassStatement(),
                TokenKind.At => ParseDecoratedDef(),
                TokenKind.AtIdentifier when IsDecoratorContext() => ParseDecoratedDef(),
                _ => ParseFieldOrStatement(),
            };

            members.Add(member);
            SkipNewlines();
        }

        if (Current.Kind == TokenKind.Dedent)
            Advance();

        _inClassBody = prev;
        return members;
    }

    private AstNode ParseFieldOrStatement()
    {
        // Could be a field declaration (name: type = default) or a statement
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
        {
            var start = Current.Span.Start;
            var name = Expect(TokenKind.Identifier).Lexeme;
            Expect(TokenKind.Colon);
            var type = ParseTypeAnnotation();

            Expression? defaultVal = null;
            if (TryConsume(TokenKind.Assign))
                defaultVal = ParseExpression();

            return new FieldDeclaration(name, type, defaultVal, new SourceSpan(start, CurrentLocation()));
        }

        return ParseStatement() ?? new PassStatement(Current.Span);
    }

    private PropertyDef ParsePropertyDef()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'prop'
        var name = Expect(TokenKind.Identifier).Lexeme;
        Expect(TokenKind.Arrow);
        var type = ParseTypeAnnotation();
        Expect(TokenKind.Colon);

        Block? getter = null;
        Block? setter = null;

        ExpectNewlineAndIndent();

        while (!IsAtEnd && Current.Kind != TokenKind.Dedent)
        {
            SkipNewlines();
            if (Current.Kind == TokenKind.Dedent) break;

            if (Current.Kind == TokenKind.KwGet)
            {
                Advance();
                Expect(TokenKind.Colon);
                // Single-line getter
                if (Current.Kind != TokenKind.Newline && Current.Kind != TokenKind.Indent)
                {
                    var stmt = ParseStatement();
                    getter = stmt is not null
                        ? new Block([stmt], stmt.Span)
                        : null;
                }
                else
                {
                    getter = ParseBlock();
                }
            }
            else if (Current.Kind == TokenKind.KwSet)
            {
                Advance();
                Expect(TokenKind.Colon);
                if (Current.Kind != TokenKind.Newline && Current.Kind != TokenKind.Indent)
                {
                    var stmt = ParseStatement();
                    setter = stmt is not null
                        ? new Block([stmt], stmt.Span)
                        : null;
                }
                else
                {
                    setter = ParseBlock();
                }
            }
            else
            {
                _diagnostics.Error("LEB1002", "Expected 'get' or 'set' in property definition", Current.Span);
                Advance();
            }

            SkipNewlines();
        }

        if (Current.Kind == TokenKind.Dedent)
            Advance();

        return new PropertyDef(name, type, getter, setter, new SourceSpan(start, CurrentLocation()));
    }

    // ─── Decorator ───

    /// <summary>
    /// Determines if an AtIdentifier token at statement level is a decorator
    /// (followed by def, class, async, or another decorator on the next line).
    /// </summary>
    private bool IsDecoratorContext()
    {
        // Look ahead past newlines (and optional argument list) to see if we reach
        // def/class/async/@/@identifier
        var lookahead = _pos + 1;

        // Skip past decorator arguments: @Route("/api") → skip (...) block
        if (lookahead < _tokens.Count && _tokens[lookahead].Kind == TokenKind.LeftParen)
        {
            var depth = 1;
            lookahead++;
            while (lookahead < _tokens.Count && depth > 0)
            {
                if (_tokens[lookahead].Kind == TokenKind.LeftParen) depth++;
                else if (_tokens[lookahead].Kind == TokenKind.RightParen) depth--;
                lookahead++;
            }
        }

        while (lookahead < _tokens.Count && _tokens[lookahead].Kind == TokenKind.Newline)
            lookahead++;
        if (lookahead >= _tokens.Count) return false;
        var nextKind = _tokens[lookahead].Kind;
        return nextKind is TokenKind.KwDef or TokenKind.KwAsync or TokenKind.KwClass
            or TokenKind.At or TokenKind.AtIdentifier;
    }

    private Statement ParseDecoratedDef()
    {
        var decorators = new List<Decorator>();

        while (Current.Kind == TokenKind.At || Current.Kind == TokenKind.AtIdentifier)
        {
            var start = Current.Span.Start;

            if (Current.Kind == TokenKind.AtIdentifier)
            {
                // @Name was lexed as a single AtIdentifier token — extract the name
                var name = Current.Lexeme[1..]; // strip leading '@'
                var tok = Current;
                Advance();
                Expression decoratorExpr = new IdentifierExpr(name, tok.Span);

                // Check for decorator arguments: @Route("/api")
                if (Current.Kind == TokenKind.LeftParen)
                {
                    Advance(); // consume '('
                    var args = ParseArgumentList();
                    var endLoc = CurrentLocation();
                    Expect(TokenKind.RightParen);
                    decoratorExpr = new CallExpr(decoratorExpr, args,
                        new SourceSpan(start, endLoc));
                }

                decorators.Add(new Decorator(decoratorExpr,
                    new SourceSpan(start, decoratorExpr.Span.End)));
            }
            else
            {
                Advance(); // consume '@'

                // Decorator can be native keyword
                if (Current.Kind == TokenKind.KwNative)
                {
                    var nativeTok = Current;
                    Advance();
                    decorators.Add(new Decorator(
                        new IdentifierExpr("native", nativeTok.Span),
                        new SourceSpan(start, nativeTok.Span.End)));
                }
                else
                {
                    var expr = ParseExpression();
                    decorators.Add(new Decorator(expr, new SourceSpan(start, expr.Span.End)));
                }
            }

            SkipNewlines();
        }

        if (Current.Kind == TokenKind.KwDef)
            return ParseFunctionDef(isAsync: false, decorators);
        if (Current.Kind == TokenKind.KwAsync)
        {
            Advance();
            return ParseFunctionDef(isAsync: true, decorators);
        }
        if (Current.Kind == TokenKind.KwClass)
            return ParseClassDef(decorators);

        _diagnostics.Error("LEB1003", "Decorator must be followed by a function or class definition", Current.Span);
        return new PassStatement(Current.Span);
    }

    // ─── Control Flow ───

    private IfStatement ParseIfStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'if'
        var condition = ParseExpression();
        Expect(TokenKind.Colon);
        var body = ParseBlock();

        var elifs = new List<ElifClause>();
        Block? elseBody = null;

        while (Current.Kind == TokenKind.KwElif)
        {
            var elifStart = Current.Span.Start;
            Advance();
            var elifCond = ParseExpression();
            Expect(TokenKind.Colon);
            var elifBody = ParseBlock();
            elifs.Add(new ElifClause(elifCond, elifBody, new SourceSpan(elifStart, elifBody.Span.End)));
        }

        if (Current.Kind == TokenKind.KwElse)
        {
            Advance();
            Expect(TokenKind.Colon);
            elseBody = ParseBlock();
        }

        var end = elseBody?.Span.End ?? (elifs.Count > 0 ? elifs[^1].Span.End : body.Span.End);
        return new IfStatement(condition, body, elifs, elseBody, new SourceSpan(start, end));
    }

    private WhileStatement ParseWhileStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'while'
        var condition = ParseExpression();
        Expect(TokenKind.Colon);
        var body = ParseBlock();

        Block? elseBody = null;
        if (Current.Kind == TokenKind.KwElse)
        {
            Advance();
            Expect(TokenKind.Colon);
            elseBody = ParseBlock();
        }

        var end = elseBody?.Span.End ?? body.Span.End;
        return new WhileStatement(condition, body, elseBody, new SourceSpan(start, end));
    }

    private ForStatement ParseForStatement(bool isAsync = false)
    {
        var start = Current.Span.Start;
        Advance(); // consume 'for'
        var variable = Expect(TokenKind.Identifier).Lexeme;
        Expect(TokenKind.KwIn);
        var iterable = ParseExpression();
        Expect(TokenKind.Colon);
        var body = ParseBlock();

        Block? elseBody = null;
        if (Current.Kind == TokenKind.KwElse)
        {
            Advance();
            Expect(TokenKind.Colon);
            elseBody = ParseBlock();
        }

        var end = elseBody?.Span.End ?? body.Span.End;
        return new ForStatement(variable, iterable, body, elseBody, isAsync, new SourceSpan(start, end));
    }

    private WithStatement ParseWithStatement(bool isAsync = false)
    {
        var start = Current.Span.Start;
        Advance(); // consume 'with'

        var items = new List<WithItem>();
        items.Add(ParseWithItem());
        while (TryConsume(TokenKind.Comma))
            items.Add(ParseWithItem());

        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new WithStatement(items, body, isAsync, new SourceSpan(start, body.Span.End));
    }

    private WithItem ParseWithItem()
    {
        var start = Current.Span.Start;
        var expr = ParseExpression();
        string? variable = null;
        if (TryConsume(TokenKind.KwAs))
            variable = Expect(TokenKind.Identifier).Lexeme;
        return new WithItem(expr, variable, new SourceSpan(start, CurrentLocation()));
    }

    private TryStatement ParseTryStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'try'
        Expect(TokenKind.Colon);
        var body = ParseBlock();

        var exceptClauses = new List<ExceptClause>();
        while (Current.Kind == TokenKind.KwExcept)
        {
            var excStart = Current.Span.Start;
            Advance();

            TypeAnnotation? exType = null;
            string? variable = null;
            List<TypeAnnotation>? multiTypes = null;

            if (Current.Kind != TokenKind.Colon)
            {
                // Check for multiple exception types: except (ValueError, TypeError) as e:
                if (Current.Kind == TokenKind.LeftParen)
                {
                    Advance(); // consume '('
                    multiTypes = new List<TypeAnnotation> { ParseTypeAnnotation() };
                    while (TryConsume(TokenKind.Comma))
                    {
                        if (Current.Kind == TokenKind.RightParen) break;
                        multiTypes.Add(ParseTypeAnnotation());
                    }
                    Expect(TokenKind.RightParen);
                }
                else
                {
                    exType = ParseTypeAnnotation();
                }

                if (TryConsume(TokenKind.KwAs))
                    variable = Expect(TokenKind.Identifier).Lexeme;
            }

            Expect(TokenKind.Colon);
            var excBody = ParseBlock();

            if (multiTypes is not null)
            {
                // Desugar: produce one ExceptClause per type, all sharing the same body and variable
                foreach (var mt in multiTypes)
                    exceptClauses.Add(new ExceptClause(mt, variable, excBody, new SourceSpan(excStart, excBody.Span.End)));
            }
            else
            {
                exceptClauses.Add(new ExceptClause(exType, variable, excBody, new SourceSpan(excStart, excBody.Span.End)));
            }
        }

        Block? finallyBody = null;
        if (Current.Kind == TokenKind.KwFinally)
        {
            Advance();
            Expect(TokenKind.Colon);
            finallyBody = ParseBlock();
        }

        var end = finallyBody?.Span.End ?? (exceptClauses.Count > 0 ? exceptClauses[^1].Span.End : body.Span.End);
        return new TryStatement(body, exceptClauses, finallyBody, new SourceSpan(start, end));
    }

    private RaiseStatement ParseRaiseStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'raise'
        Expression? value = null;
        Expression? cause = null;
        if (Current.Kind != TokenKind.Newline && Current.Kind != TokenKind.EndOfFile)
        {
            value = ParseExpression();
            if (TryConsume(TokenKind.KwFrom))
                cause = ParseExpression();
        }
        return new RaiseStatement(value, cause, new SourceSpan(start, CurrentLocation()));
    }

    private AssertStatement ParseAssertStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'assert'
        var condition = ParseExpression();
        Expression? message = null;
        if (TryConsume(TokenKind.Comma))
            message = ParseExpression();
        return new AssertStatement(condition, message, new SourceSpan(start, CurrentLocation()));
    }

    // ─── Type Alias ───

    private TypeAliasStatement ParseTypeAlias()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'type'
        var name = Expect(TokenKind.Identifier).Lexeme;
        Expect(TokenKind.Assign);
        var target = ParseTypeAnnotation();
        return new TypeAliasStatement(name, null, target, new SourceSpan(start, CurrentLocation()));
    }

    // ─── Match Statement ───

    private MatchStatement ParseMatchStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'match'
        var subject = ParseExpression();
        Expect(TokenKind.Colon);

        var cases = new List<MatchCase>();
        ExpectNewlineAndIndent();

        while (!IsAtEnd && Current.Kind != TokenKind.Dedent)
        {
            SkipNewlines();
            if (Current.Kind == TokenKind.Dedent) break;
            cases.Add(ParseMatchCase());
            SkipNewlines();
        }

        if (Current.Kind == TokenKind.Dedent)
            Advance();

        return new MatchStatement(subject, cases, new SourceSpan(start, CurrentLocation()));
    }

    private MatchCase ParseMatchCase()
    {
        var start = Current.Span.Start;
        Expect(TokenKind.KwCase);
        var pattern = ParsePattern();

        // OR patterns: case 1 | 2 | 3:
        if (Current.Kind == TokenKind.Pipe)
        {
            var alternatives = new List<Pattern> { pattern };
            while (TryConsume(TokenKind.Pipe))
                alternatives.Add(ParsePattern());
            pattern = new OrPattern(alternatives, new SourceSpan(start, CurrentLocation()));
        }

        Expression? guard = null;
        if (TryConsume(TokenKind.KwIf))
            guard = ParseExpression();

        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new MatchCase(pattern, guard, body, new SourceSpan(start, body.Span.End));
    }

    private Pattern ParsePattern()
    {
        var start = Current.Span.Start;

        if (Current.Kind == TokenKind.Underscore)
        {
            Advance();
            return new WildcardPattern(SourceSpan.From(start));
        }

        if (Current.Kind == TokenKind.NoneLiteral)
        {
            Advance();
            return new NonePattern(SourceSpan.From(start));
        }

        if (Current.Kind is TokenKind.IntegerLiteral or TokenKind.FloatLiteral or TokenKind.StringLiteral or TokenKind.BoolLiteral)
        {
            var literal = ParsePrimary();
            return new LiteralPattern(literal, literal.Span);
        }

        if (Current.Kind == TokenKind.Identifier)
        {
            var name = Current.Lexeme;
            Advance();

            // Constructor pattern: Type(fields...)
            if (Current.Kind == TokenKind.LeftParen)
            {
                Advance();
                var fields = new List<Pattern>();
                if (Current.Kind != TokenKind.RightParen)
                {
                    fields.Add(ParsePattern());
                    while (TryConsume(TokenKind.Comma))
                        fields.Add(ParsePattern());
                }
                Expect(TokenKind.RightParen);
                return new ConstructorPattern(name, fields, new SourceSpan(start, CurrentLocation()));
            }

            // Dotted name for enum variants: Shape.Circle(r)
            if (Current.Kind == TokenKind.Dot)
            {
                Advance();
                var variant = Expect(TokenKind.Identifier).Lexeme;
                var fullName = $"{name}.{variant}";

                if (Current.Kind == TokenKind.LeftParen)
                {
                    Advance();
                    var fields = new List<Pattern>();
                    if (Current.Kind != TokenKind.RightParen)
                    {
                        fields.Add(ParsePattern());
                        while (TryConsume(TokenKind.Comma))
                            fields.Add(ParsePattern());
                    }
                    Expect(TokenKind.RightParen);
                    return new ConstructorPattern(fullName, fields, new SourceSpan(start, CurrentLocation()));
                }

                return new NamePattern(fullName, new SourceSpan(start, CurrentLocation()));
            }

            return new NamePattern(name, new SourceSpan(start, CurrentLocation()));
        }

        _diagnostics.Error("LEB1004", $"Unexpected token in pattern: {Current.Kind}", Current.Span);
        Advance();
        return new WildcardPattern(SourceSpan.From(start));
    }

    // ─── When Statement (Conditional Compilation) ───

    private WhenStatement ParseWhenStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'when'
        var target = Expect(TokenKind.KwTarget).Lexeme;
        var comparison = Expect(TokenKind.Equal).Lexeme; // ==
        var value = Expect(TokenKind.StringLiteral).Lexeme;
        Expect(TokenKind.Colon);
        var body = ParseBlock();
        return new WhenStatement(target, comparison, value, body, new SourceSpan(start, body.Span.End));
    }

    // ─── Simple Statements ───

    private ReturnStatement ParseReturnStatement()
    {
        var start = Current.Span.Start;
        Advance();
        Expression? value = null;
        if (Current.Kind != TokenKind.Newline && Current.Kind != TokenKind.EndOfFile && Current.Kind != TokenKind.Dedent)
        {
            value = ParseExpression();
            // Implicit tuple: return a, b → return (a, b)
            if (Current.Kind == TokenKind.Comma)
            {
                var elements = new List<Expression> { value };
                while (TryConsume(TokenKind.Comma))
                {
                    if (Current.Kind is TokenKind.Newline or TokenKind.EndOfFile or TokenKind.Dedent)
                        break;
                    elements.Add(ParseExpression());
                }
                value = new TupleExpr(elements,
                    new SourceSpan(elements[0].Span.Start, elements[^1].Span.End));
            }
        }
        return new ReturnStatement(value, new SourceSpan(start, CurrentLocation()));
    }

    private YieldStatement ParseYieldStatement()
    {
        var start = Current.Span.Start;
        Advance();
        Expression? value = null;
        if (Current.Kind != TokenKind.Newline && Current.Kind != TokenKind.EndOfFile)
            value = ParseExpression();
        return new YieldStatement(value, new SourceSpan(start, CurrentLocation()));
    }

    private BreakStatement ParseBreakStatement()
    {
        var tok = Current;
        Advance();
        return new BreakStatement(tok.Span);
    }

    private ContinueStatement ParseContinueStatement()
    {
        var tok = Current;
        Advance();
        return new ContinueStatement(tok.Span);
    }

    private PassStatement ParsePassStatement()
    {
        var tok = Current;
        Advance();
        return new PassStatement(tok.Span);
    }

    // ─── Imports ───

    private ImportStatement ParseImportStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'import'
        var path = ParseDottedName();
        string? alias = null;
        if (TryConsume(TokenKind.KwAs))
            alias = Expect(TokenKind.Identifier).Lexeme;
        return new ImportStatement(path, alias, new SourceSpan(start, CurrentLocation()));
    }

    private FromImportStatement ParseFromImportStatement()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'from'
        var path = ParseDottedName();
        Expect(TokenKind.KwImport);

        var names = new List<ImportName>();
        names.Add(ParseImportName());
        while (TryConsume(TokenKind.Comma))
            names.Add(ParseImportName());

        return new FromImportStatement(path, names, new SourceSpan(start, CurrentLocation()));
    }

    private ImportName ParseImportName()
    {
        var start = Current.Span.Start;
        var name = Expect(TokenKind.Identifier).Lexeme;
        string? alias = null;
        if (TryConsume(TokenKind.KwAs))
            alias = Expect(TokenKind.Identifier).Lexeme;
        return new ImportName(name, alias, new SourceSpan(start, CurrentLocation()));
    }

    private string ParseDottedName()
    {
        var parts = new List<string>();
        parts.Add(Expect(TokenKind.Identifier).Lexeme);
        while (TryConsume(TokenKind.Dot))
            parts.Add(Expect(TokenKind.Identifier).Lexeme);
        return string.Join('.', parts);
    }

    // ─── Expression or Assignment ───

    private Statement ParseExpressionOrAssignment()
    {
        var start = Current.Span.Start;
        // Starred expression at start of statement: *rest, ... = ...
        Expression expr;
        if (Current.Kind == TokenKind.Star && Peek(1).Kind == TokenKind.Identifier)
        {
            var starStart = Current.Span.Start;
            Advance(); // consume *
            var operand = ParsePrimary();
            expr = new StarredExpr(operand, new SourceSpan(starStart, operand.Span.End));
        }
        else
        {
            expr = ParseExpression();
        }

        // Tuple unpacking: a, b = b, a  /  x, y, z = 10, 20, 30
        // When we see Comma at statement level after the first expression,
        // collect targets into a TupleExpr, then expect '=' and parse RHS as tuple.
        // Also check if the FIRST expr should be starred: *rest, ... = ...
        if (Current.Kind == TokenKind.Comma || expr is StarredExpr)
        {
            var targets = new List<Expression> { expr };
            while (TryConsume(TokenKind.Comma))
            {
                if (Current.Kind == TokenKind.Star)
                {
                    var starStart = Current.Span.Start;
                    Advance();
                    var operand = ParsePrimary();
                    targets.Add(new StarredExpr(operand, new SourceSpan(starStart, operand.Span.End)));
                }
                else
                {
                    targets.Add(ParseExpression());
                }
            }
            var tupleTarget = new TupleExpr(targets,
                new SourceSpan(targets[0].Span.Start, targets[^1].Span.End));

            if (Current.Kind == TokenKind.Assign)
            {
                Advance();
                var firstValue = ParseExpression();
                if (Current.Kind == TokenKind.Comma)
                {
                    var values = new List<Expression> { firstValue };
                    while (TryConsume(TokenKind.Comma))
                    {
                        values.Add(ParseExpression());
                    }
                    var tupleValue = new TupleExpr(values,
                        new SourceSpan(values[0].Span.Start, values[^1].Span.End));
                    return new AssignmentStatement(tupleTarget, tupleValue,
                        new SourceSpan(start, CurrentLocation()));
                }
                // Single value on RHS: a, b = some_tuple()
                return new AssignmentStatement(tupleTarget, firstValue,
                    new SourceSpan(start, CurrentLocation()));
            }

            // No assignment — this is a tuple expression statement: a, b
            return new ExpressionStatement(tupleTarget,
                new SourceSpan(start, CurrentLocation()));
        }

        // Annotated assignment: name: type = value
        if (expr is IdentifierExpr ident && Current.Kind == TokenKind.Colon)
        {
            Advance();
            var type = ParseTypeAnnotation();
            Expression? value = null;
            if (TryConsume(TokenKind.Assign))
                value = ParseExpression();
            return new AnnotatedAssignment(ident.Name, type, value, new SourceSpan(start, CurrentLocation()));
        }

        // Simple assignment (with chained assignment support): target = value
        // a = b = c = 0 → desugars to c = 0; b = c; a = b
        if (Current.Kind == TokenKind.Assign)
        {
            Advance();
            var value = ParseExpression();

            // Check for chained assignment: if value is an identifier and next token is '='
            if (Current.Kind == TokenKind.Assign && value is IdentifierExpr)
            {
                // Collect all targets: [a, b, c, ...] and final value
                var targets = new List<Expression> { expr, value };
                while (Current.Kind == TokenKind.Assign)
                {
                    Advance(); // consume '='
                    var next = ParseExpression();
                    if (Current.Kind == TokenKind.Assign && next is IdentifierExpr)
                    {
                        targets.Add(next);
                    }
                    else
                    {
                        // 'next' is the final value
                        // Desugar: targets = [a, b, c], finalValue = 0
                        // Produce: c = 0; b = c; a = b
                        var stmts = new List<Statement>();
                        var lastTarget = targets[^1];
                        stmts.Add(new AssignmentStatement(lastTarget, next,
                            new SourceSpan(lastTarget.Span.Start, next.Span.End)));
                        for (int i = targets.Count - 2; i >= 0; i--)
                        {
                            stmts.Add(new AssignmentStatement(targets[i], lastTarget,
                                new SourceSpan(targets[i].Span.Start, lastTarget.Span.End)));
                            lastTarget = targets[i];
                        }
                        return new CompoundStatement(stmts, new SourceSpan(start, CurrentLocation()));
                    }
                }
            }

            return new AssignmentStatement(expr, value, new SourceSpan(start, CurrentLocation()));
        }

        // Augmented assignment: target += value, etc.
        if (IsAugmentedAssign(Current.Kind))
        {
            var op = Current.Kind;
            Advance();
            var value = ParseExpression();
            return new AugmentedAssignmentStatement(expr, op, value, new SourceSpan(start, CurrentLocation()));
        }

        return new ExpressionStatement(expr, new SourceSpan(start, CurrentLocation()));
    }

    private static bool IsAugmentedAssign(TokenKind kind) => kind is
        TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign or
        TokenKind.SlashAssign or TokenKind.PercentAssign or TokenKind.DoubleSlashAssign or
        TokenKind.DoubleStarAssign or TokenKind.AmpersandAssign or TokenKind.PipeAssign or
        TokenKind.CaretAssign or TokenKind.ShiftLeftAssign or TokenKind.ShiftRightAssign;

    // ─── Expression Parsing (Pratt / Precedence Climbing) ───

    private Expression ParseExpression() => ParseConditional();

    private Expression ParseConditional()
    {
        var expr = ParseOr();

        // Record with-expression: expr with (field=value, ...)
        if (Current.Kind == TokenKind.KwWith)
        {
            Advance();
            Expect(TokenKind.LeftParen);
            var updates = new List<(string Name, Expression Value)>();
            do
            {
                var fieldName = Expect(TokenKind.Identifier).Lexeme;
                Expect(TokenKind.Assign);
                var value = ParseExpression();
                updates.Add((fieldName, value));
            } while (TryConsume(TokenKind.Comma));
            var endLoc = CurrentLocation();
            Expect(TokenKind.RightParen);
            return new WithExpr(expr, updates, new SourceSpan(expr.Span.Start, endLoc));
        }

        // Ternary: value if condition else other
        if (Current.Kind == TokenKind.KwIf)
        {
            Advance();
            var condition = ParseOr();
            Expect(TokenKind.KwElse);
            var falseExpr = ParseExpression();
            return new ConditionalExpr(condition, expr, falseExpr,
                new SourceSpan(expr.Span.Start, falseExpr.Span.End));
        }

        return expr;
    }

    private Expression ParseOr()
    {
        var left = ParseAnd();
        while (Current.Kind == TokenKind.KwOr)
        {
            var op = Current.Kind;
            Advance();
            var right = ParseAnd();
            left = new BinaryExpr(left, op, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseAnd()
    {
        var left = ParseNot();
        while (Current.Kind == TokenKind.KwAnd)
        {
            var op = Current.Kind;
            Advance();
            var right = ParseNot();
            left = new BinaryExpr(left, op, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseNot()
    {
        if (Current.Kind == TokenKind.KwNot)
        {
            var start = Current.Span.Start;
            Advance();
            var operand = ParseNot();
            return new UnaryExpr(TokenKind.KwNot, operand, new SourceSpan(start, operand.Span.End));
        }
        return ParseComparison();
    }

    private Expression ParseComparison()
    {
        var left = ParseBitwiseOr();

        while (Current.Kind is TokenKind.Equal or TokenKind.NotEqual or TokenKind.LessThan or
               TokenKind.GreaterThan or TokenKind.LessEqual or TokenKind.GreaterEqual or
               TokenKind.KwIs or TokenKind.KwIn)
        {
            if (Current.Kind == TokenKind.KwIs)
            {
                var start = left.Span.Start;
                Advance();
                var negated = TryConsume(TokenKind.KwNot);

                // "is None" check
                if (Current.Kind == TokenKind.NoneLiteral)
                {
                    Advance();
                    var noneType = new SimpleType("None", SourceSpan.From(CurrentLocation()));
                    left = new IsExpr(left, noneType, negated, new SourceSpan(start, CurrentLocation()));
                }
                else
                {
                    var type = ParseTypeAnnotation();
                    left = new IsExpr(left, type, negated, new SourceSpan(start, type.Span.End));
                }
                continue;
            }

            if (Current.Kind == TokenKind.KwIn)
            {
                Advance();
                var right = ParseBitwiseOr();
                left = new InExpr(left, right, false, new SourceSpan(left.Span.Start, right.Span.End));
                continue;
            }

            // Check for "not in"
            if (Current.Kind == TokenKind.KwNot && Peek(1).Kind == TokenKind.KwIn)
            {
                Advance(); Advance();
                var right = ParseBitwiseOr();
                left = new InExpr(left, right, true, new SourceSpan(left.Span.Start, right.Span.End));
                continue;
            }

            var op = Current.Kind;
            Advance();
            var rhs = ParseBitwiseOr();
            left = new BinaryExpr(left, op, rhs, new SourceSpan(left.Span.Start, rhs.Span.End));

            // Comparison chaining: a < b < c → (a < b) and (b < c)
            // If next token is also a standard comparison operator, desugar into 'and' chain.
            while (IsStandardComparisonOp(Current.Kind))
            {
                var chainOp = Current.Kind;
                Advance();
                var chainRight = ParseBitwiseOr();
                var chainComparison = new BinaryExpr(rhs, chainOp, chainRight,
                    new SourceSpan(rhs.Span.Start, chainRight.Span.End));
                left = new BinaryExpr(left, TokenKind.KwAnd, chainComparison,
                    new SourceSpan(left.Span.Start, chainRight.Span.End));
                rhs = chainRight; // for further chaining: a < b < c < d
            }
        }

        return left;
    }

    private static bool IsStandardComparisonOp(TokenKind kind) =>
        kind is TokenKind.Equal or TokenKind.NotEqual or TokenKind.LessThan or
               TokenKind.GreaterThan or TokenKind.LessEqual or TokenKind.GreaterEqual;

    private Expression ParseBitwiseOr()
    {
        var left = ParseBitwiseXor();
        while (Current.Kind == TokenKind.Pipe)
        {
            Advance();
            var right = ParseBitwiseXor();
            left = new BinaryExpr(left, TokenKind.Pipe, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseBitwiseXor()
    {
        var left = ParseBitwiseAnd();
        while (Current.Kind == TokenKind.Caret)
        {
            Advance();
            var right = ParseBitwiseAnd();
            left = new BinaryExpr(left, TokenKind.Caret, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseBitwiseAnd()
    {
        var left = ParseShift();
        while (Current.Kind == TokenKind.Ampersand)
        {
            Advance();
            var right = ParseShift();
            left = new BinaryExpr(left, TokenKind.Ampersand, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseShift()
    {
        var left = ParseAddSub();
        while (Current.Kind is TokenKind.ShiftLeft or TokenKind.ShiftRight)
        {
            var op = Current.Kind;
            Advance();
            var right = ParseAddSub();
            left = new BinaryExpr(left, op, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseAddSub()
    {
        var left = ParseMulDiv();
        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = Current.Kind;
            Advance();
            var right = ParseMulDiv();
            left = new BinaryExpr(left, op, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseMulDiv()
    {
        var left = ParseUnary();
        while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.DoubleSlash or TokenKind.Percent)
        {
            var op = Current.Kind;
            Advance();
            var right = ParseUnary();
            left = new BinaryExpr(left, op, right, new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseUnary()
    {
        if (Current.Kind is TokenKind.Minus or TokenKind.Plus or TokenKind.Tilde)
        {
            var start = Current.Span.Start;
            var op = Current.Kind;
            Advance();
            var operand = ParseUnary();
            return new UnaryExpr(op, operand, new SourceSpan(start, operand.Span.End));
        }
        return ParsePower();
    }

    private Expression ParsePower()
    {
        var left = ParseAwait();
        if (Current.Kind == TokenKind.DoubleStar)
        {
            Advance();
            var right = ParseUnary(); // Right-associative
            return new BinaryExpr(left, TokenKind.DoubleStar, right,
                new SourceSpan(left.Span.Start, right.Span.End));
        }
        return left;
    }

    private Expression ParseAwait()
    {
        if (Current.Kind == TokenKind.KwAwait)
        {
            var start = Current.Span.Start;
            Advance();
            var operand = ParseUnary();
            return new AwaitExpr(operand, new SourceSpan(start, operand.Span.End));
        }
        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        var expr = ParsePrimary();

        while (true)
        {
            if (Current.Kind == TokenKind.LeftParen)
            {
                // Function call
                Advance();
                var args = ParseArgumentList();
                var end = Expect(TokenKind.RightParen).Span.End;
                expr = new CallExpr(expr, args, new SourceSpan(expr.Span.Start, end));
            }
            else if (Current.Kind == TokenKind.LeftBracket)
            {
                // Index or slice
                Advance();
                expr = ParseIndexOrSlice(expr);
            }
            else if (Current.Kind == TokenKind.Dot)
            {
                // Member access — allow contextual keywords as member names
                Advance();
                string member;
                if (Current.Kind == TokenKind.Identifier || Current.Kind is TokenKind.KwGet or TokenKind.KwSet
                    or TokenKind.KwType or TokenKind.KwFrom)
                {
                    member = Current.Lexeme;
                    Advance();
                }
                else
                {
                    member = Expect(TokenKind.Identifier).Lexeme;
                }
                expr = new MemberAccessExpr(expr, member, new SourceSpan(expr.Span.Start, CurrentLocation()));
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private List<Argument> ParseArgumentList()
    {
        var args = new List<Argument>();
        if (Current.Kind == TokenKind.RightParen)
            return args;

        args.Add(ParseArgument());
        while (TryConsume(TokenKind.Comma))
        {
            if (Current.Kind == TokenKind.RightParen)
                break;
            args.Add(ParseArgument());
        }

        return args;
    }

    private Argument ParseArgument()
    {
        var start = Current.Span.Start;

        // Check for call-site unpacking: *args
        if (Current.Kind == TokenKind.Star)
        {
            Advance(); // consume '*'
            var unpackExpr = ParseExpression();
            return new Argument(null, unpackExpr, true, new SourceSpan(start, unpackExpr.Span.End));
        }

        // Check for named argument: name=value
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Assign)
        {
            var name = Current.Lexeme;
            Advance(); // name
            Advance(); // =
            var value = ParseExpression();
            return new Argument(name, value, false, new SourceSpan(start, value.Span.End));
        }

        var expr = ParseExpression();

        // Generator expression inside function call: list(x * 2 for x in items)
        // The function call's parentheses serve as the generator's parentheses.
        if (Current.Kind == TokenKind.KwFor)
        {
            var clauses = ParseComprehensionClauses();
            var genExpr = new GeneratorExpr(expr, clauses,
                new SourceSpan(start, CurrentLocation()));
            return new Argument(null, genExpr, false, new SourceSpan(start, genExpr.Span.End));
        }

        return new Argument(null, expr, false, new SourceSpan(start, expr.Span.End));
    }

    private Expression ParseIndexOrSlice(Expression obj)
    {
        var start = obj.Span.Start;

        // Could be slice: [a:b:c]
        Expression? lower = null, upper = null, step = null;

        if (Current.Kind != TokenKind.Colon)
            lower = ParseExpression();

        if (Current.Kind == TokenKind.Colon)
        {
            Advance();
            if (Current.Kind != TokenKind.Colon && Current.Kind != TokenKind.RightBracket)
                upper = ParseExpression();
            if (TryConsume(TokenKind.Colon))
            {
                if (Current.Kind != TokenKind.RightBracket)
                    step = ParseExpression();
            }
            var end = Expect(TokenKind.RightBracket).Span.End;
            return new SliceExpr(obj, lower, upper, step, new SourceSpan(start, end));
        }

        // Multi-type generic arguments: Dict[str, int], Tuple[int, str], etc.
        if (Current.Kind == TokenKind.Comma && lower is not null)
        {
            var elements = new List<Expression> { lower };
            while (TryConsume(TokenKind.Comma))
            {
                elements.Add(ParseExpression());
            }
            var endMulti = Expect(TokenKind.RightBracket).Span.End;
            var tupleIndex = new TupleExpr(elements, new SourceSpan(lower.Span.Start, endMulti));
            return new IndexExpr(obj, tupleIndex, new SourceSpan(start, endMulti));
        }

        var endTok = Expect(TokenKind.RightBracket).Span.End;
        return new IndexExpr(obj, lower!, new SourceSpan(start, endTok));
    }

    private Expression ParsePrimary()
    {
        var tok = Current;

        switch (tok.Kind)
        {
            case TokenKind.IntegerLiteral:
                Advance();
                return new IntLiteralExpr(
                    tok.LiteralValue is long l ? l : 0L,
                    tok.Span);

            case TokenKind.FloatLiteral:
                Advance();
                return new FloatLiteralExpr(
                    tok.LiteralValue is double d ? d : 0.0,
                    tok.Span);

            case TokenKind.StringLiteral:
                Advance();
                return new StringLiteralExpr(tok.Lexeme, tok.Span);

            case TokenKind.FStringLiteral:
                Advance();
                return ParseFStringParts(tok.Lexeme, tok.Span);

            case TokenKind.BoolLiteral:
                Advance();
                return new BoolLiteralExpr(tok.LiteralValue is true, tok.Span);

            case TokenKind.NoneLiteral:
                Advance();
                return new NoneLiteralExpr(tok.Span);

            case TokenKind.Identifier:
            case TokenKind.KwType:
            case TokenKind.KwSet:  // soft keyword — identifier in expression context
            case TokenKind.KwGet:  // soft keyword — identifier in expression context
                Advance();
                return new IdentifierExpr(tok.Lexeme, tok.Span);

            case TokenKind.AtIdentifier:
                Advance();
                return new FieldAccessExpr(tok.Lexeme[1..], tok.Span); // strip '@'

            case TokenKind.KwLambda:
                return ParseLambda();

            case TokenKind.LeftParen:
                return ParseParenOrTuple();

            case TokenKind.LeftBracket:
                return ParseListOrComprehension();

            case TokenKind.LeftBrace:
                return ParseDictOrSet();

            default:
                _diagnostics.Error("LEB1005", $"Unexpected token: {tok.Kind} '{tok.Lexeme}'", tok.Span);
                Advance();
                return new NoneLiteralExpr(tok.Span);
        }
    }

    private Expression ParseLambda()
    {
        var start = Current.Span.Start;
        Advance(); // consume 'lambda'

        var parameters = new List<Parameter>();
        if (Current.Kind != TokenKind.Colon)
        {
            parameters.Add(ParseLambdaParam());
            while (TryConsume(TokenKind.Comma))
                parameters.Add(ParseLambdaParam());
        }

        Expect(TokenKind.Colon);
        var body = ParseExpression();

        return new LambdaExpr(parameters, body, new SourceSpan(start, body.Span.End));
    }

    private Parameter ParseLambdaParam()
    {
        var start = Current.Span.Start;
        var name = Expect(TokenKind.Identifier).Lexeme;
        // Lambda params don't support inline type annotations to avoid
        // ambiguity with the body separator colon. Use typed lambdas
        // with explicit Func/Action types instead.
        TypeAnnotation type = new SimpleType("object", SourceSpan.From(start));
        return new Parameter(name, type, null, false, new SourceSpan(start, CurrentLocation()));
    }

    private Expression ParseParenOrTuple()
    {
        var start = Current.Span.Start;
        Advance(); // consume '('

        if (Current.Kind == TokenKind.RightParen)
        {
            Advance();
            return new TupleExpr([], new SourceSpan(start, CurrentLocation()));
        }

        var first = ParseExpression();

        // Generator expression: (x for x in ...)
        if (Current.Kind == TokenKind.KwFor)
            return ParseGeneratorExpr(first, start);

        // Tuple with comma
        if (Current.Kind == TokenKind.Comma)
        {
            var elements = new List<Expression> { first };
            while (TryConsume(TokenKind.Comma))
            {
                if (Current.Kind == TokenKind.RightParen)
                    break;
                elements.Add(ParseExpression());
            }
            Expect(TokenKind.RightParen);
            return new TupleExpr(elements, new SourceSpan(start, CurrentLocation()));
        }

        // Parenthesized expression
        Expect(TokenKind.RightParen);
        return first;
    }

    private Expression ParseListOrComprehension()
    {
        var start = Current.Span.Start;
        Advance(); // consume '['

        if (Current.Kind == TokenKind.RightBracket)
        {
            Advance();
            return new ListExpr([], new SourceSpan(start, CurrentLocation()));
        }

        var first = ParseExpression();

        // List comprehension: [x for x in ... (for y in ... if ...)*]
        if (Current.Kind == TokenKind.KwFor)
        {
            var clauses = ParseComprehensionClauses();
            Expect(TokenKind.RightBracket);
            return new ListComprehension(first, clauses,
                new SourceSpan(start, CurrentLocation()));
        }

        // Regular list
        var elements = new List<Expression> { first };
        while (TryConsume(TokenKind.Comma))
        {
            if (Current.Kind == TokenKind.RightBracket)
                break;
            elements.Add(ParseExpression());
        }
        Expect(TokenKind.RightBracket);
        return new ListExpr(elements, new SourceSpan(start, CurrentLocation()));
    }

    private Expression ParseDictOrSet()
    {
        var start = Current.Span.Start;
        Advance(); // consume '{'

        if (Current.Kind == TokenKind.RightBrace)
        {
            Advance();
            return new DictExpr([], new SourceSpan(start, CurrentLocation()));
        }

        var first = ParseExpression();

        // Dict: {key: value, ...}
        if (Current.Kind == TokenKind.Colon)
        {
            Advance();
            var firstVal = ParseExpression();

            // Dict comprehension
            if (Current.Kind == TokenKind.KwFor)
            {
                var clauses = ParseComprehensionClauses();
                Expect(TokenKind.RightBrace);
                return new DictComprehension(first, firstVal, clauses,
                    new SourceSpan(start, CurrentLocation()));
            }

            var entries = new List<(Expression, Expression)> { (first, firstVal) };
            while (TryConsume(TokenKind.Comma))
            {
                if (Current.Kind == TokenKind.RightBrace) break;
                var key = ParseExpression();
                Expect(TokenKind.Colon);
                var val = ParseExpression();
                entries.Add((key, val));
            }
            Expect(TokenKind.RightBrace);
            return new DictExpr(entries, new SourceSpan(start, CurrentLocation()));
        }

        // Set comprehension: {expr for var in iterable (if condition)? (for ...)*}
        if (Current.Kind == TokenKind.KwFor)
        {
            var clauses = ParseComprehensionClauses();
            Expect(TokenKind.RightBrace);
            return new SetComprehension(first, clauses,
                new SourceSpan(start, CurrentLocation()));
        }

        // Set: {a, b, c}
        var setElements = new List<Expression> { first };
        while (TryConsume(TokenKind.Comma))
        {
            if (Current.Kind == TokenKind.RightBrace) break;
            setElements.Add(ParseExpression());
        }
        Expect(TokenKind.RightBrace);
        return new SetExpr(setElements, new SourceSpan(start, CurrentLocation()));
    }

    private GeneratorExpr ParseGeneratorExpr(Expression element, SourceLocation start)
    {
        var clauses = ParseComprehensionClauses();
        Expect(TokenKind.RightParen);
        return new GeneratorExpr(element, clauses,
            new SourceSpan(start, CurrentLocation()));
    }

    /// <summary>
    /// Parses one or more comprehension clauses: for var in iterable [if cond] ...
    /// Current token must be KwFor on entry.
    /// </summary>
    private List<ComprehensionClause> ParseComprehensionClauses()
    {
        var clauses = new List<ComprehensionClause>();
        while (Current.Kind == TokenKind.KwFor)
        {
            var clauseStart = Current.Span.Start;
            Advance(); // consume 'for'
            var variable = Expect(TokenKind.Identifier).Lexeme;
            Expect(TokenKind.KwIn);
            var iterable = ParseOr();
            Expression? condition = null;
            if (TryConsume(TokenKind.KwIf))
                condition = ParseOr();
            clauses.Add(new ComprehensionClause(variable, iterable, condition,
                new SourceSpan(clauseStart, CurrentLocation())));
        }
        return clauses;
    }

    // ─── F-String Parsing ───

    private FStringExpr ParseFStringParts(string raw, SourceSpan span)
    {
        var parts = new List<FStringPart>();
        var i = 0;
        var textStart = 0;

        while (i < raw.Length)
        {
            if (raw[i] == '{')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '{')
                {
                    // Escaped brace
                    i += 2;
                    continue;
                }

                // Emit text before this
                if (i > textStart)
                    parts.Add(new FStringText(raw[textStart..i], span));

                // Find matching }
                var braceDepth = 1;
                var exprStart = i + 1;
                i++;
                while (i < raw.Length && braceDepth > 0)
                {
                    if (raw[i] == '{') braceDepth++;
                    else if (raw[i] == '}') braceDepth--;
                    if (braceDepth > 0) i++;
                }

                var exprText = raw[exprStart..i];
                if (i < raw.Length) i++; // skip '}'
                textStart = i;

                // Split on ':' for format spec (respect nested braces/brackets/parens)
                string? formatSpec = null;
                int fmtDepth = 0;
                for (int ci = 0; ci < exprText.Length; ci++)
                {
                    char c = exprText[ci];
                    if (c is '(' or '[' or '{') fmtDepth++;
                    else if (c is ')' or ']' or '}') fmtDepth--;
                    else if (c == ':' && fmtDepth == 0)
                    {
                        formatSpec = exprText[(ci + 1)..];
                        exprText = exprText[..ci];
                        break;
                    }
                }

                // Parse the expression inside {}
                var exprLexer = new CulebralLexer(exprText, "<fstring>", _diagnostics);
                var exprTokens = exprLexer.Tokenize();
                var exprParser = new CulebralParser(exprTokens, _diagnostics);
                var expr = exprParser.ParseExpression();
                parts.Add(new FStringInterpolation(expr, formatSpec, span));
            }
            else
            {
                i++;
            }
        }

        if (textStart < raw.Length)
            parts.Add(new FStringText(raw[textStart..], span));

        return new FStringExpr(parts, span);
    }

    // ─── Type Annotations ───

    private TypeAnnotation ParseTypeAnnotation()
    {
        var start = Current.Span.Start;
        TypeAnnotation type;

        // Tuple type: (int, str)
        if (Current.Kind == TokenKind.LeftParen)
        {
            Advance();
            var elements = new List<TupleTypeElement>();
            elements.Add(ParseTupleTypeElement());
            while (TryConsume(TokenKind.Comma))
                elements.Add(ParseTupleTypeElement());
            Expect(TokenKind.RightParen);
            type = new TupleType(elements, new SourceSpan(start, CurrentLocation()));
        }
        else if (Current.Kind == TokenKind.NoneLiteral)
        {
            Advance();
            type = new SimpleType("None", new SourceSpan(start, CurrentLocation()));
        }
        else
        {
            var name = Expect(TokenKind.Identifier).Lexeme;

            // Generic type: list[int], dict[str, int]
            if (Current.Kind == TokenKind.LeftBracket)
            {
                Advance();
                var typeArgs = new List<TypeAnnotation>();
                typeArgs.Add(ParseTypeAnnotation());
                while (TryConsume(TokenKind.Comma))
                    typeArgs.Add(ParseTypeAnnotation());
                Expect(TokenKind.RightBracket);
                type = new GenericType(name, typeArgs, new SourceSpan(start, CurrentLocation()));
            }
            else
            {
                type = new SimpleType(name, new SourceSpan(start, CurrentLocation()));
            }
        }

        // Nullable: Type?
        if (Current.Kind == TokenKind.Question)
        {
            Advance();
            type = new NullableType(type, new SourceSpan(start, CurrentLocation()));
        }

        return type;
    }

    private TupleTypeElement ParseTupleTypeElement()
    {
        var start = Current.Span.Start;

        // Check for named element: name: type
        if (Current.Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.Colon)
        {
            var name = Current.Lexeme;
            Advance();
            Advance(); // consume ':'
            var type = ParseTypeAnnotation();
            return new TupleTypeElement(name, type, new SourceSpan(start, CurrentLocation()));
        }

        var elemType = ParseTypeAnnotation();
        return new TupleTypeElement(null, elemType, new SourceSpan(start, CurrentLocation()));
    }

    private List<TypeParameter>? TryParseTypeParameters()
    {
        if (Current.Kind != TokenKind.LeftBracket)
            return null;

        Advance();
        var parameters = new List<TypeParameter>();

        var start = Current.Span.Start;
        var name = Expect(TokenKind.Identifier).Lexeme;
        TypeAnnotation? constraint = null;
        if (TryConsume(TokenKind.Colon))
            constraint = ParseTypeAnnotation();
        parameters.Add(new TypeParameter(name, constraint, new SourceSpan(start, CurrentLocation())));

        while (TryConsume(TokenKind.Comma))
        {
            start = Current.Span.Start;
            name = Expect(TokenKind.Identifier).Lexeme;
            constraint = null;
            if (TryConsume(TokenKind.Colon))
                constraint = ParseTypeAnnotation();
            parameters.Add(new TypeParameter(name, constraint, new SourceSpan(start, CurrentLocation())));
        }

        Expect(TokenKind.RightBracket);
        return parameters;
    }

    // ─── Block Parsing ───

    private Block ParseBlock()
    {
        var statements = new List<Statement>();

        // Single-line block (after colon, on same line)
        if (Current.Kind != TokenKind.Newline && Current.Kind != TokenKind.EndOfFile)
        {
            var stmt = ParseStatement();
            if (stmt is not null)
                statements.Add(stmt);
            var span2 = statements.Count > 0
                ? new SourceSpan(statements[0].Span.Start, statements[^1].Span.End)
                : SourceSpan.None;
            return new Block(statements, span2);
        }

        ExpectNewlineAndIndent();

        while (!IsAtEnd && Current.Kind != TokenKind.Dedent)
        {
            SkipNewlines();
            if (Current.Kind == TokenKind.Dedent) break;

            var stmt = ParseStatement();
            if (stmt is not null)
                statements.Add(stmt);
            SkipNewlines();
        }

        if (Current.Kind == TokenKind.Dedent)
            Advance();

        var blockSpan = statements.Count > 0
            ? new SourceSpan(statements[0].Span.Start, statements[^1].Span.End)
            : SourceSpan.None;
        return new Block(statements, blockSpan);
    }

    private void ExpectNewlineAndIndent()
    {
        // Consume optional newlines
        while (Current.Kind == TokenKind.Newline)
            Advance();

        if (Current.Kind == TokenKind.Indent)
            Advance();
        else if (Current.Kind != TokenKind.EndOfFile)
            _diagnostics.Error("LEB1006", "Expected indented block", Current.Span);
    }

    // ─── Token Helpers ───

    private Token Current => _pos < _tokens.Count ? _tokens[_pos] : _tokens[^1];

    private Token Peek(int offset)
    {
        var idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : _tokens[^1];
    }

    private bool IsAtEnd => _pos >= _tokens.Count || Current.Kind == TokenKind.EndOfFile;

    private void Advance()
    {
        if (_pos < _tokens.Count)
            _pos++;
    }

    private Token Expect(TokenKind kind)
    {
        if (Current.Kind == kind)
        {
            var tok = Current;
            Advance();
            return tok;
        }

        _diagnostics.Error("LEB1007", $"Expected {kind}, got {Current.Kind} '{Current.Lexeme}'", Current.Span);
        return Current;
    }

    private bool TryConsume(TokenKind kind)
    {
        if (Current.Kind == kind)
        {
            Advance();
            return true;
        }
        return false;
    }

    private void SkipNewlines()
    {
        while (Current.Kind == TokenKind.Newline)
            Advance();
    }

    private SourceLocation CurrentLocation()
    {
        return Current.Span.Start;
    }
}
