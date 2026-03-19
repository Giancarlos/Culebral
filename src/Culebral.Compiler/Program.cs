using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Emit;
using Culebral.Compiler.IR;
using Culebral.Compiler.Lexer;
using Culebral.Compiler.NuGet;
using Culebral.Compiler.Parser;
using Culebral.Compiler.Semantics;

namespace Culebral.Compiler;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0];
        return command switch
        {
            "build" => HandleBuild(args[1..]),
            "run" => HandleRun(args[1..]),
            "check" => HandleCheck(args[1..]),
            "lex" => HandleLex(args[1..]),
            "parse" => HandleParse(args[1..]),
            "ir" => HandleIr(args[1..]),
            "--version" or "-v" => HandleVersion(),
            "--help" or "-h" => HandleHelp(),
            _ => UnknownCommand(command),
        };
    }

    private static int HandleBuild(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Error: No input file specified.");
            Console.Error.WriteLine("Usage: culebral build <file.leb> [--output <path>]");
            return 1;
        }

        var inputPath = args[0];
        var outputPath = GetOutputPath(args, inputPath);

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: File not found: {inputPath}");
            return 1;
        }

        var result = Compile(inputPath, outputPath);
        if (result.Success)
        {
            Console.WriteLine($"  Compiled: {inputPath} -> {outputPath}");
            return 0;
        }

        Console.Error.Write(result.Diagnostics.FormatAll());
        return 1;
    }

    private static int HandleRun(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Error: No input file specified.");
            return 1;
        }

        var inputPath = args[0];
        var tempDir = Path.Combine(Path.GetTempPath(), "culebral_run");
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(inputPath) + ".dll");

        var result = Compile(inputPath, outputPath);
        if (!result.Success)
        {
            Console.Error.Write(result.Diagnostics.FormatAll());
            return 1;
        }

        // Run with dotnet
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = outputPath,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null)
        {
            Console.Error.WriteLine("Error: Failed to start dotnet process.");
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    private static int HandleCheck(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Error: No input file specified.");
            return 1;
        }

        var inputPath = args[0];
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: File not found: {inputPath}");
            return 1;
        }

        var source = File.ReadAllText(inputPath);
        var diagnostics = new DiagnosticBag();

        // Lex
        var lexer = new CulebralLexer(source, inputPath, diagnostics);
        var tokens = lexer.Tokenize();
        if (diagnostics.HasErrors)
        {
            Console.Error.Write(diagnostics.FormatAll());
            return 1;
        }

        // Parse
        var parser = new CulebralParser(tokens, diagnostics);
        var ast = parser.ParseCompilationUnit();
        if (diagnostics.HasErrors)
        {
            Console.Error.Write(diagnostics.FormatAll());
            return 1;
        }

        // Type check
        var typeChecker = new TypeChecker(diagnostics);
        typeChecker.Check(ast);

        if (diagnostics.HasErrors)
        {
            Console.Error.Write(diagnostics.FormatAll());
            return 1;
        }

        Console.WriteLine($"  OK: {inputPath} ({diagnostics.Count} diagnostics)");
        if (diagnostics.Count > 0)
            Console.Write(diagnostics.FormatAll());

        return 0;
    }

    private static int HandleLex(string[] args)
    {
        if (args.Length == 0) return 1;

        var source = File.ReadAllText(args[0]);
        var diagnostics = new DiagnosticBag();
        var lexer = new CulebralLexer(source, args[0], diagnostics);
        var tokens = lexer.Tokenize();

        foreach (var token in tokens)
        {
            Console.WriteLine($"  {token.Kind,-20} {EscapeLexeme(token.Lexeme),-30} {token.Span.Start}");
        }

        if (diagnostics.HasErrors)
            Console.Error.Write(diagnostics.FormatAll());

        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int HandleParse(string[] args)
    {
        if (args.Length == 0) return 1;

        var source = File.ReadAllText(args[0]);
        var diagnostics = new DiagnosticBag();
        var lexer = new CulebralLexer(source, args[0], diagnostics);
        var tokens = lexer.Tokenize();
        var parser = new CulebralParser(tokens, diagnostics);
        var ast = parser.ParseCompilationUnit();

        PrintAst(ast, 0);

        if (diagnostics.HasErrors)
            Console.Error.Write(diagnostics.FormatAll());

        return diagnostics.HasErrors ? 1 : 0;
    }

    private static int HandleIr(string[] args)
    {
        if (args.Length == 0) return 1;

        var source = File.ReadAllText(args[0]);
        var diagnostics = new DiagnosticBag();
        var lexer = new CulebralLexer(source, args[0], diagnostics);
        var tokens = lexer.Tokenize();
        var parser = new CulebralParser(tokens, diagnostics);
        var ast = parser.ParseCompilationUnit();
        var typeChecker = new TypeChecker(diagnostics);
        typeChecker.Check(ast);

        if (diagnostics.HasErrors)
        {
            Console.Error.Write(diagnostics.FormatAll());
            return 1;
        }

        var lowering = new IrLowering(diagnostics, typeChecker);
        var moduleName = Path.GetFileNameWithoutExtension(args[0]);
        var module = lowering.Lower(ast, moduleName, args[0]);

        PrintIr(module);
        return 0;
    }

    // ─── Core Compilation Pipeline ───

    public static CompilationResult Compile(string inputPath, string outputPath)
    {
        var source = File.ReadAllText(inputPath);
        var diagnostics = new DiagnosticBag();

        // Phase 0: NuGet Resolution (if culebral.toml exists)
        var tomlPath = FindProjectFile(inputPath);
        if (tomlPath is not null)
        {
            var projectFile = ProjectFileParser.Parse(tomlPath);
            if (projectFile.Dependencies.Count > 0)
            {
                var nugetResolver = new NuGetResolver(diagnostics);
                nugetResolver.Resolve(projectFile);
                // NuGet errors are non-fatal — compilation continues
                // with whatever types are already available
            }
        }

        // Phase 1: Lexing
        var lexer = new CulebralLexer(source, inputPath, diagnostics);
        var tokens = lexer.Tokenize();
        if (diagnostics.HasErrors)
            return new CompilationResult(false, diagnostics);

        // Phase 2: Parsing
        var parser = new CulebralParser(tokens, diagnostics);
        var ast = parser.ParseCompilationUnit();
        if (diagnostics.HasErrors)
            return new CompilationResult(false, diagnostics);

        // Phase 3: Type Checking
        var typeChecker = new TypeChecker(diagnostics);
        typeChecker.Check(ast);
        if (diagnostics.HasErrors)
            return new CompilationResult(false, diagnostics);

        // Phase 4: IR Lowering
        var lowering = new IrLowering(diagnostics, typeChecker);
        var moduleName = Path.GetFileNameWithoutExtension(inputPath);
        var module = lowering.Lower(ast, moduleName, inputPath);
        if (diagnostics.HasErrors)
            return new CompilationResult(false, diagnostics);

        // Phase 5: CIL Emission
        var emitter = new CilEmitter(diagnostics, outputPath);
        var success = emitter.Emit(module);

        return new CompilationResult(success, diagnostics);
    }

    /// <summary>
    /// Search for a culebral.toml project file starting from the source file's directory
    /// and walking up to parent directories.
    /// </summary>
    private static string? FindProjectFile(string sourceFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath));
        while (dir is not null)
        {
            var tomlPath = Path.Combine(dir, "culebral.toml");
            if (File.Exists(tomlPath))
                return tomlPath;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ─── Output Helpers ───

    private static void PrintAst(AstNode node, int depth)
    {
        var indent = new string(' ', depth * 2);
        Console.WriteLine($"{indent}{node.GetType().Name}");

        switch (node)
        {
            case CompilationUnit cu:
                foreach (var stmt in cu.Statements) PrintAst(stmt, depth + 1);
                break;
            case FunctionDef func:
                Console.WriteLine($"{indent}  name: {func.Name}");
                Console.WriteLine($"{indent}  params: [{string.Join(", ", func.Parameters.Select(p => $"{p.Name}: {p.Type}"))}]");
                if (func.ReturnType is not null)
                    Console.WriteLine($"{indent}  returns: {func.ReturnType}");
                PrintAst(func.Body, depth + 1);
                break;
            case Block block:
                foreach (var stmt in block.Statements) PrintAst(stmt, depth + 1);
                break;
            case ExpressionStatement exprStmt:
                PrintAst(exprStmt.Expr, depth + 1);
                break;
            case CallExpr call:
                Console.Write($"{indent}  callee: ");
                PrintAst(call.Callee, 0);
                foreach (var arg in call.Arguments) PrintAst(arg.Value, depth + 2);
                break;
            case ReturnStatement ret:
                if (ret.Value is not null) PrintAst(ret.Value, depth + 1);
                break;
            case AssignmentStatement assign:
                PrintAst(assign.Target, depth + 1);
                PrintAst(assign.Value, depth + 1);
                break;
            case IdentifierExpr ident:
                Console.WriteLine($"{indent}  name: {ident.Name}");
                break;
            case StringLiteralExpr str:
                Console.WriteLine($"{indent}  value: \"{str.Value}\"");
                break;
            case IntLiteralExpr i:
                Console.WriteLine($"{indent}  value: {i.Value}");
                break;
        }
    }

    private static void PrintIr(IrModule module)
    {
        Console.WriteLine($"Module: {module.Name}");
        Console.WriteLine($"  Source: {module.SourcePath}");
        Console.WriteLine();

        foreach (var type in module.Types)
        {
            Console.WriteLine($"  type {type.Kind} {type.Name}:");
            foreach (var field in type.Fields)
                Console.WriteLine($"    field {field.Name}: {field.Type.DisplayName}");
            foreach (var method in type.Methods)
                PrintIrFunction(method, "    ");
            Console.WriteLine();
        }

        foreach (var func in module.Functions)
        {
            PrintIrFunction(func, "  ");
            Console.WriteLine();
        }

        if (module.EntryPoint is not null)
            Console.WriteLine($"  entry: {module.EntryPoint.Name}");
    }

    private static void PrintIrFunction(IrFunction func, string indent)
    {
        var paramsStr = string.Join(", ", func.Parameters.Select(p => $"{p.Name}: {p.Type.DisplayName}"));
        var asyncStr = func.IsAsync ? "async " : "";
        Console.WriteLine($"{indent}{asyncStr}fn {func.Name}({paramsStr}) -> {func.ReturnType.DisplayName}:");
        Console.WriteLine($"{indent}  locals: [{string.Join(", ", func.Locals.Select(l => $"{l.Name}: {l.Type.DisplayName}"))}]");

        foreach (var block in func.Body)
        {
            Console.WriteLine($"{indent}  {block.Label}:");
            foreach (var instr in block.Instructions)
            {
                Console.WriteLine($"{indent}    {instr}");
            }
        }
    }

    private static string EscapeLexeme(string lexeme)
    {
        return lexeme.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    private static string GetOutputPath(string[] args, string inputPath)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--output" or "-o")
                return args[i + 1];
        }
        return Path.ChangeExtension(inputPath, ".dll");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Culebral Compiler");
        Console.WriteLine();
        Console.WriteLine("Usage: culebral <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  build <file.leb>     Compile a Culebral source file to .NET assembly");
        Console.WriteLine("  run <file.leb>       Compile and run a Culebral source file");
        Console.WriteLine("  check <file.leb>     Type-check without compiling");
        Console.WriteLine("  lex <file.leb>       Print lexer tokens (debug)");
        Console.WriteLine("  parse <file.leb>     Print parse tree (debug)");
        Console.WriteLine("  ir <file.leb>        Print CulebralIR (debug)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --output, -o <path>  Output file path");
        Console.WriteLine("  --version, -v        Show version");
        Console.WriteLine("  --help, -h           Show this help");
    }

    private static int HandleVersion()
    {
        Console.WriteLine("culebral 0.1.0-alpha");
        return 0;
    }

    private static int HandleHelp()
    {
        PrintUsage();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Error: Unknown command '{command}'");
        PrintUsage();
        return 1;
    }
}

public sealed record CompilationResult(bool Success, DiagnosticBag Diagnostics);
