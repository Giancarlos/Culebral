using System.Text;
using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Emit;
using Culebral.Compiler.IR;
using Culebral.Compiler.Lexer;
using Culebral.Compiler.Lsp;
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
            "test" => HandleTest(args[1..]),
            "fmt" => HandleFmt(args[1..]),
            "lex" => HandleLex(args[1..]),
            "parse" => HandleParse(args[1..]),
            "ir" => HandleIr(args[1..]),
            "repl" => HandleRepl(),
            "lsp" => HandleLsp(),
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

    // ─── LSP Server ───

    private static int HandleLsp()
    {
        var server = new LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput());
        server.Run();
        return 0;
    }

    // ─── REPL ───

    private static int HandleRepl()
    {
        Console.WriteLine("Culebral 0.1.0-alpha (interactive mode)");
        Console.WriteLine("Type expressions or statements. Blank line after indented block to execute.");
        Console.WriteLine("Type 'exit' or 'quit' to leave.");
        Console.WriteLine();

        var definitions = new StringBuilder();

        while (true)
        {
            Console.Write(">>> ");
            var line = Console.ReadLine();
            if (line is null) break; // EOF
            if (line.Trim() is "exit" or "quit") break;

            // Detect multi-line input
            var input = new StringBuilder(line);
            if (line.TrimEnd().EndsWith(':'))
            {
                while (true)
                {
                    Console.Write("... ");
                    var cont = Console.ReadLine();
                    if (cont is null || cont.Trim() == "") break;
                    input.AppendLine();
                    input.Append(cont);
                }
            }

            var code = input.ToString().Trim();
            if (string.IsNullOrEmpty(code)) continue;

            // Check if it's a definition (def, class, struct, record, enum, interface, import, from, type, async def)
            bool isDef = code.StartsWith("def ") || code.StartsWith("class ") ||
                         code.StartsWith("struct ") || code.StartsWith("record ") ||
                         code.StartsWith("enum ") || code.StartsWith("interface ") ||
                         code.StartsWith("import ") || code.StartsWith("from ") ||
                         code.StartsWith("type ") || code.StartsWith("async def ");

            if (isDef)
            {
                // Save current definitions in case we need to roll back
                var previousDefs = definitions.ToString();
                definitions.AppendLine(code);
                definitions.AppendLine();

                // Try to compile to check for errors
                var testSource = definitions + "def main():\n    pass\n";
                var (success, _, errors) = ExecuteSource(testSource, "<repl>");
                if (!success)
                {
                    // Roll back the bad definition
                    definitions.Clear();
                    definitions.Append(previousDefs);
                    Console.Error.WriteLine(errors);
                }
            }
            else
            {
                // Expression or statement — wrap in main() and execute
                var indented = string.Join("\n", code.Split('\n').Select(l => "    " + l));
                var source = definitions + $"def main():\n{indented}\n";

                var (success, output, errors) = ExecuteSource(source, "<repl>");
                if (success)
                {
                    if (!string.IsNullOrEmpty(output))
                        Console.Write(output);
                }
                else
                {
                    Console.Error.WriteLine(errors);
                }
            }
        }

        return 0;
    }

    // ─── Test Runner ───

    private static int HandleTest(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: culebral test <file.leb>");
            return 1;
        }

        var filePath = args[0];
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"Error: File not found: {filePath}");
            return 1;
        }

        var source = File.ReadAllText(filePath);

        // Parse to find test functions
        var diagnostics = new DiagnosticBag();
        var lexer = new CulebralLexer(source, filePath, diagnostics);
        var tokens = lexer.Tokenize();
        if (diagnostics.HasErrors)
        {
            Console.Error.Write(diagnostics.FormatAll());
            return 1;
        }

        var parser = new CulebralParser(tokens, diagnostics);
        var ast = parser.ParseCompilationUnit();
        if (diagnostics.HasErrors)
        {
            Console.Error.Write(diagnostics.FormatAll());
            return 1;
        }

        var testFunctions = ast.Statements
            .OfType<FunctionDef>()
            .Where(f => f.Name.StartsWith("test_"))
            .Select(f => f.Name)
            .ToList();

        if (testFunctions.Count == 0)
        {
            Console.WriteLine("No test functions found (functions must start with 'test_')");
            return 0;
        }

        Console.WriteLine($"Running {testFunctions.Count} tests...");

        // Generate a main() that calls each test function wrapped in try/except
        var runner = new StringBuilder();
        runner.AppendLine(source);
        runner.AppendLine();
        runner.AppendLine("def main():");
        runner.AppendLine("    __passed = 0");
        runner.AppendLine("    __failed = 0");

        foreach (var testName in testFunctions)
        {
            runner.AppendLine("    try:");
            runner.AppendLine($"        {testName}()");
            runner.AppendLine($"        print(\"  {testName} ... PASS\")");
            runner.AppendLine("        __passed += 1");
            runner.AppendLine("    except Exception as __e:");
            runner.AppendLine($"        print(f\"  {testName} ... FAIL: {{__e}}\")");
            runner.AppendLine("        __failed += 1");
        }

        runner.AppendLine();
        runner.AppendLine("    print()");
        runner.AppendLine("    print(f\"{__passed} passed, {__failed} failed\")");
        runner.AppendLine("    if __failed > 0:");
        runner.AppendLine("        raise Exception(\"Tests failed\")");

        var testSource = runner.ToString();

        // Compile and run the generated source
        var (success, output, errors) = ExecuteSource(testSource, filePath);

        if (!string.IsNullOrEmpty(output))
            Console.Write(output);

        if (success)
        {
            return 0;
        }
        else
        {
            // If compilation failed, show compiler errors
            if (!string.IsNullOrEmpty(errors))
                Console.Error.Write(errors);
            return 1;
        }
    }

    // ─── Formatter ───

    private static int HandleFmt(string[] args)
    {
        bool checkOnly = false;
        string? filePath = null;

        foreach (var arg in args)
        {
            if (arg is "--check") checkOnly = true;
            else filePath = arg;
        }

        if (filePath is null)
        {
            Console.Error.WriteLine("Usage: culebral fmt [--check] <file.leb>");
            return 1;
        }

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"Error: File not found: {filePath}");
            return 1;
        }

        var original = File.ReadAllText(filePath);
        var formatted = FormatSource(original);

        if (checkOnly)
        {
            if (original == formatted)
            {
                Console.WriteLine($"  {filePath}: already formatted");
                return 0;
            }
            else
            {
                Console.WriteLine($"  {filePath}: would be reformatted");
                return 1;
            }
        }

        if (original != formatted)
        {
            File.WriteAllText(filePath, formatted);
            Console.WriteLine($"  Formatted: {filePath}");
        }
        else
        {
            Console.WriteLine($"  {filePath}: already formatted");
        }
        return 0;
    }

    public static string FormatSource(string source)
    {
        var lines = source.Split('\n');
        var result = new List<string>();
        int consecutiveBlanks = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd(); // strip trailing whitespace (including \r)

            if (string.IsNullOrWhiteSpace(line))
            {
                consecutiveBlanks++;
                if (consecutiveBlanks <= 2) // collapse 3+ blanks to 2
                    result.Add("");
                continue;
            }

            // Two blank lines before top-level def/class (not indented)
            if (!line.StartsWith(' ') && !line.StartsWith('\t') &&
                (line.StartsWith("def ") || line.StartsWith("async def ") ||
                 line.StartsWith("class ") || line.StartsWith("struct ") ||
                 line.StartsWith("record ") || line.StartsWith("enum ") ||
                 line.StartsWith("interface ")))
            {
                // Ensure exactly 2 blank lines before (unless it's the first non-blank content)
                if (result.Count > 0)
                {
                    // Remove existing trailing blanks
                    while (result.Count > 0 && string.IsNullOrEmpty(result[^1]))
                        result.RemoveAt(result.Count - 1);
                    // Add exactly 2 blank lines
                    if (result.Count > 0)
                    {
                        result.Add("");
                        result.Add("");
                    }
                }
            }

            consecutiveBlanks = 0;
            result.Add(line);
        }

        // Ensure single trailing newline
        while (result.Count > 0 && string.IsNullOrEmpty(result[^1]))
            result.RemoveAt(result.Count - 1);

        return string.Join("\n", result) + "\n";
    }

    // ─── Core Compilation Pipeline ───

    public static CompilationResult Compile(string inputPath, string outputPath)
    {
        var source = File.ReadAllText(inputPath);
        var diagnostics = new DiagnosticBag();

        // Phase 0: NuGet Resolution (if project file exists)
        List<string>? frameworkRefs = null;
        string? targetFramework = null;
        var projectFilePath = FindProjectFile(inputPath);
        if (projectFilePath is not null)
        {
            if (projectFilePath.EndsWith(".lebproj", StringComparison.OrdinalIgnoreCase))
            {
                // .lebproj — MSBuild XML format. Run dotnet restore on the real project,
                // then read assembly paths from project.assets.json.
                var lebProject = LebProjectParser.Parse(projectFilePath);
                targetFramework = lebProject.TargetFramework;
                frameworkRefs = lebProject.FrameworkReferences;

                if (lebProject.Dependencies.Count > 0)
                {
                    // Resolve NuGet packages via dotnet restore + project.assets.json
                    var assetsReader = new ProjectAssetsReader(diagnostics);
                    if (assetsReader.RestoreAndResolve(projectFilePath))
                    {
                        assetsReader.LoadResolvedAssemblies();
                    }

                    // Also resolve framework references from the shared runtime
                    // (these aren't in project.assets.json — they ship with .NET)
                    var projectFile = lebProject.ToProjectFileParser();
                    var nugetResolver = new NuGetResolver(diagnostics);
                    nugetResolver.ResolveFrameworkReferencesOnly(projectFile);
                    // NuGet errors are non-fatal — compilation continues
                }
            }
            else
            {
                // culebral.toml — legacy format
                var projectFile = ProjectFileParser.Parse(projectFilePath);
                targetFramework = projectFile.TargetFramework;
                if (projectFile.Dependencies.Count > 0)
                {
                    var nugetResolver = new NuGetResolver(diagnostics);
                    nugetResolver.Resolve(projectFile);
                    frameworkRefs = nugetResolver.GetFrameworkReferences(projectFile);
                    // NuGet errors are non-fatal — compilation continues
                }
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
        if (frameworkRefs is not null)
            emitter.FrameworkReferences.AddRange(frameworkRefs);
        if (targetFramework is not null)
            emitter.TargetFramework = targetFramework;
        var success = emitter.Emit(module);

        return new CompilationResult(success, diagnostics);
    }

    /// <summary>
    /// Compile Culebral source code from a string, writing the output assembly to <paramref name="outputPath"/>.
    /// This decouples the input side from the filesystem — the source is provided directly.
    /// </summary>
    /// <param name="source">The Culebral source code.</param>
    /// <param name="outputPath">Path where the compiled .dll will be written.</param>
    /// <param name="sourceName">Name used in diagnostics for error messages (default: "&lt;script&gt;").</param>
    public static CompilationResult CompileFromSource(string source, string outputPath, string sourceName = "<script>")
    {
        var diagnostics = new DiagnosticBag();

        // Phase 1: Lexing
        var lexer = new CulebralLexer(source, sourceName, diagnostics);
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
        var moduleName = Path.GetFileNameWithoutExtension(sourceName);
        var module = lowering.Lower(ast, moduleName, sourceName);
        if (diagnostics.HasErrors)
            return new CompilationResult(false, diagnostics);

        // Phase 5: CIL Emission
        var emitter = new CilEmitter(diagnostics, outputPath);
        var success = emitter.Emit(module);

        return new CompilationResult(success, diagnostics);
    }

    /// <summary>
    /// Compile and execute Culebral source code in a temporary directory, capturing stdout/stderr.
    /// Cleans up temp files after execution.
    /// </summary>
    /// <param name="source">The Culebral source code.</param>
    /// <param name="sourceName">Name used in diagnostics (default: "&lt;script&gt;").</param>
    /// <returns>A tuple of (Success, stdout Output, stderr Errors).</returns>
    public static (bool Success, string Output, string Errors) ExecuteSource(string source, string sourceName = "<script>")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"culebral_exec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var moduleName = Path.GetFileNameWithoutExtension(sourceName);
            if (moduleName == "<script>") moduleName = "script";
            var dllPath = Path.Combine(tempDir, moduleName + ".dll");

            var result = CompileFromSource(source, dllPath, sourceName);
            if (!result.Success)
            {
                return (false, string.Empty, result.Diagnostics.FormatAll());
            }

            // Run the compiled assembly
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = dllPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return (false, string.Empty, "Failed to start dotnet process.");
            }

            var output = process.StandardOutput.ReadToEnd();
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode == 0, output, errors);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Search for a project file starting from the source file's directory
    /// and walking up to parent directories.
    /// Prefers .lebproj files (MSBuild XML) over culebral.toml (legacy format).
    /// </summary>
    private static string? FindProjectFile(string sourceFilePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath));
        while (dir is not null)
        {
            // Prefer .lebproj files (MSBuild XML format)
            var lebprojFiles = Directory.GetFiles(dir, "*.lebproj");
            if (lebprojFiles.Length > 0)
                return lebprojFiles[0];

            // Fall back to culebral.toml (legacy format)
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
        Console.WriteLine("  test <file.leb>      Discover and run test_ functions");
        Console.WriteLine("  fmt <file.leb>       Format source file to canonical style");
        Console.WriteLine("  lex <file.leb>       Print lexer tokens (debug)");
        Console.WriteLine("  parse <file.leb>     Print parse tree (debug)");
        Console.WriteLine("  ir <file.leb>        Print CulebralIR (debug)");
        Console.WriteLine("  repl                 Start interactive REPL session");
        Console.WriteLine("  lsp                  Start Language Server Protocol server");
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
