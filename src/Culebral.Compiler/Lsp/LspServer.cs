using System.Text;
using System.Text.Json;
using Culebral.Compiler.Diagnostics;
using Culebral.Compiler.Lexer;
using Culebral.Compiler.Parser;
using Culebral.Compiler.Semantics;

namespace Culebral.Compiler.Lsp;

/// <summary>
/// Minimal LSP server implementing the Language Server Protocol over JSON-RPC 2.0 on stdin/stdout.
/// Supports diagnostics (errors/warnings as you type) for Culebral source files.
/// </summary>
public sealed class LspServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Dictionary<string, string> _openDocuments = new(); // uri -> content
    private bool _shutdown;

    public LspServer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public void Run()
    {
        while (!_shutdown)
        {
            var message = ReadMessage();
            if (message is null) break;
            HandleMessage(message);
        }
    }

    // ─── LSP Message I/O ───

    /// <summary>
    /// Reads an LSP message from the input stream.
    /// Format: "Content-Length: N\r\n\r\n{json body of N bytes}"
    /// </summary>
    private JsonDocument? ReadMessage()
    {
        // Read headers until we get an empty line
        int contentLength = -1;
        while (true)
        {
            var headerLine = ReadLine();
            if (headerLine is null) return null; // stream closed

            if (headerLine.Length == 0)
            {
                // End of headers
                break;
            }

            if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = headerLine["Content-Length:".Length..].Trim();
                if (int.TryParse(value, out var len))
                    contentLength = len;
            }
            // Other headers (e.g. Content-Type) are ignored
        }

        if (contentLength < 0) return null;

        // Read exactly contentLength bytes
        var buffer = new byte[contentLength];
        int totalRead = 0;
        while (totalRead < contentLength)
        {
            int read = _input.Read(buffer, totalRead, contentLength - totalRead);
            if (read == 0) return null; // stream closed
            totalRead += read;
        }

        return JsonDocument.Parse(buffer);
    }

    /// <summary>
    /// Reads a single line (terminated by \r\n) from the input stream.
    /// </summary>
    private string? ReadLine()
    {
        var sb = new StringBuilder();
        int prev = -1;

        while (true)
        {
            int b = _input.ReadByte();
            if (b == -1) return sb.Length > 0 ? sb.ToString() : null;

            if (prev == '\r' && b == '\n')
            {
                // Remove the trailing \r we already appended
                sb.Length--;
                return sb.ToString();
            }

            sb.Append((char)b);
            prev = b;
        }
    }

    /// <summary>
    /// Sends an LSP message (JSON-RPC response or notification) to the output stream.
    /// </summary>
    private void SendMessage(byte[] jsonBytes)
    {
        var header = $"Content-Length: {jsonBytes.Length}\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        _output.Write(headerBytes);
        _output.Write(jsonBytes);
        _output.Flush();
    }

    private void SendResponse(JsonElement id, object? result)
    {
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = DeserializeId(id),
            result,
        });
        SendMessage(response);
    }

    private void SendError(JsonElement id, int code, string message)
    {
        var response = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = DeserializeId(id),
            error = new { code, message },
        });
        SendMessage(response);
    }

    private void SendNotification(string method, object @params)
    {
        var notification = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            method,
            @params,
        });
        SendMessage(notification);
    }

    /// <summary>
    /// Deserialize the JSON-RPC id field which can be a number or string.
    /// </summary>
    private static object? DeserializeId(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.Number => id.GetInt64(),
            JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }

    // ─── Message Dispatch ───

    private void HandleMessage(JsonDocument doc)
    {
        using (doc)
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("method", out var methodProp))
                return; // Not a request/notification we understand

            var method = methodProp.GetString();
            var hasId = root.TryGetProperty("id", out var id);
            root.TryGetProperty("params", out var @params);

            switch (method)
            {
                case "initialize":
                    HandleInitialize(id);
                    break;

                case "initialized":
                    // No action needed
                    break;

                case "shutdown":
                    _shutdown = true;
                    if (hasId) SendResponse(id, null);
                    break;

                case "exit":
                    Environment.Exit(_shutdown ? 0 : 1);
                    break;

                case "textDocument/didOpen":
                    HandleDidOpen(@params);
                    break;

                case "textDocument/didChange":
                    HandleDidChange(@params);
                    break;

                case "textDocument/didClose":
                    HandleDidClose(@params);
                    break;

                case "textDocument/didSave":
                    HandleDidChange(@params); // Re-run diagnostics on save
                    break;

                case "textDocument/hover":
                    if (hasId) HandleHover(id, @params);
                    break;

                default:
                    // Unknown method — if it's a request (has id), return method not found
                    if (hasId) SendError(id, -32601, $"Method not found: {method}");
                    break;
            }
        }
    }

    // ─── Handler Implementations ───

    private void HandleInitialize(JsonElement id)
    {
        var capabilities = new
        {
            capabilities = new
            {
                textDocumentSync = new
                {
                    openClose = true,
                    change = 1, // Full document sync (simpler than incremental)
                    save = new { includeText = false },
                },
                hoverProvider = true,
            },
            serverInfo = new
            {
                name = "culebral-lsp",
                version = "0.1.0",
            },
        };

        SendResponse(id, capabilities);
    }

    private void HandleDidOpen(JsonElement @params)
    {
        if (@params.ValueKind == JsonValueKind.Undefined) return;

        var textDocument = @params.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString()!;
        var text = textDocument.GetProperty("text").GetString()!;

        _openDocuments[uri] = text;
        PublishDiagnostics(uri, text);
    }

    private void HandleDidChange(JsonElement @params)
    {
        if (@params.ValueKind == JsonValueKind.Undefined) return;

        var textDocument = @params.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString()!;

        // With full sync (change = 1), contentChanges[0].text is the full document
        var changes = @params.GetProperty("contentChanges");
        if (changes.GetArrayLength() > 0)
        {
            var text = changes[0].GetProperty("text").GetString()!;
            _openDocuments[uri] = text;
            PublishDiagnostics(uri, text);
        }
    }

    private void HandleDidClose(JsonElement @params)
    {
        if (@params.ValueKind == JsonValueKind.Undefined) return;

        var textDocument = @params.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString()!;
        _openDocuments.Remove(uri);

        // Clear diagnostics for the closed document
        SendNotification("textDocument/publishDiagnostics", new
        {
            uri,
            diagnostics = Array.Empty<object>(),
        });
    }

    private void HandleHover(JsonElement id, JsonElement @params)
    {
        if (@params.ValueKind == JsonValueKind.Undefined)
        {
            SendResponse(id, null);
            return;
        }

        var textDocument = @params.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString()!;
        var position = @params.GetProperty("position");
        var line = position.GetProperty("line").GetInt32();
        var character = position.GetProperty("character").GetInt32();

        var hoverContent = GetHoverInfo(uri, line, character);
        if (hoverContent is null)
        {
            SendResponse(id, null);
            return;
        }

        SendResponse(id, new
        {
            contents = new
            {
                kind = "markdown",
                value = hoverContent,
            },
        });
    }

    /// <summary>
    /// Compute hover information for the given document position.
    /// Runs the compiler front-end (lex, parse, type-check) and looks up the type
    /// of the token at the requested (line, character) position.
    /// </summary>
    private string? GetHoverInfo(string uri, int line, int character)
    {
        if (!_openDocuments.TryGetValue(uri, out var source)) return null;

        var filePath = UriToFilePath(uri);
        var diagnostics = new DiagnosticBag();

        List<Lexer.Token> tokens;
        try
        {
            var lexer = new CulebralLexer(source, filePath, diagnostics);
            tokens = lexer.Tokenize();
        }
        catch
        {
            return null;
        }

        // Find token at position (LSP is 0-based, Culebral SourceLocation is 1-based)
        var targetLine = line + 1;
        var targetCol = character + 1;

        var token = tokens.FirstOrDefault(t =>
            t.Kind != Lexer.TokenKind.EndOfFile &&
            t.Span.Start.Line == targetLine &&
            t.Span.Start.Column <= targetCol &&
            t.Span.Start.Column + t.Lexeme.Length > targetCol);

        if (token.Kind == Lexer.TokenKind.EndOfFile) return null;

        // For non-identifier tokens, show the token kind
        if (token.Kind != Lexer.TokenKind.Identifier && token.Kind != Lexer.TokenKind.AtIdentifier)
        {
            return token.Kind switch
            {
                Lexer.TokenKind.IntegerLiteral => $"```\n(literal) int\n```",
                Lexer.TokenKind.FloatLiteral => $"```\n(literal) float\n```",
                Lexer.TokenKind.StringLiteral or Lexer.TokenKind.FStringLiteral => $"```\n(literal) str\n```",
                Lexer.TokenKind.BoolLiteral => $"```\n(literal) bool\n```",
                Lexer.TokenKind.NoneLiteral => $"```\n(literal) None\n```",
                _ when token.Kind.ToString().StartsWith("Kw") => $"```\n(keyword) {token.Lexeme}\n```",
                _ => null,
            };
        }

        // Parse and type check to get type information
        try
        {
            var parser = new CulebralParser(tokens, diagnostics);
            var ast = parser.ParseCompilationUnit();
            var typeChecker = new TypeChecker(diagnostics);
            typeChecker.Check(ast);

            // First: look for a resolved AST node whose span matches the token
            foreach (var (node, type) in typeChecker.ResolvedTypes)
            {
                if (node.Span.Start.Line == targetLine &&
                    node.Span.Start.Column == token.Span.Start.Column)
                {
                    var label = node switch
                    {
                        Parser.IdentifierExpr id => id.Name,
                        Parser.FieldAccessExpr fa => $"@{fa.FieldName}",
                        Parser.CallExpr call when call.Callee is Parser.IdentifierExpr ci => $"{ci.Name}(...)",
                        _ => token.Lexeme,
                    };
                    return $"```\n{label}: {type.DisplayName}\n```";
                }
            }

            // Fallback: look up the identifier name in the global scope
            var name = token.Lexeme;
            var symbol = typeChecker.GlobalScope.Lookup(name);
            if (symbol is not null)
            {
                var kindLabel = symbol.Kind switch
                {
                    Semantics.SymbolKind.Function => "(function) ",
                    Semantics.SymbolKind.Type => "(type) ",
                    Semantics.SymbolKind.Variable => "(variable) ",
                    Semantics.SymbolKind.Parameter => "(parameter) ",
                    Semantics.SymbolKind.Field => "(field) ",
                    Semantics.SymbolKind.Property => "(property) ",
                    Semantics.SymbolKind.EnumVariant => "(enum) ",
                    _ => "",
                };
                return $"```\n{kindLabel}{name}: {symbol.Type.DisplayName}\n```";
            }
        }
        catch
        {
            // If parsing/type checking fails, we still tried
        }

        return null;
    }

    // ─── Diagnostics Pipeline ───

    /// <summary>
    /// Run the compiler front-end (lex, parse, type-check) on the given source
    /// and publish diagnostics back to the editor.
    /// </summary>
    private void PublishDiagnostics(string uri, string source)
    {
        var filePath = UriToFilePath(uri);
        var bag = new DiagnosticBag();

        // Run as much of the pipeline as possible, collecting all diagnostics
        try
        {
            var lexer = new CulebralLexer(source, filePath, bag);
            var tokens = lexer.Tokenize();

            if (!bag.HasErrors)
            {
                var parser = new CulebralParser(tokens, bag);
                var ast = parser.ParseCompilationUnit();

                if (!bag.HasErrors)
                {
                    var typeChecker = new TypeChecker(bag);
                    typeChecker.Check(ast);
                }
            }
        }
        catch
        {
            // If the compiler throws unexpectedly, we still want to report
            // whatever diagnostics were collected before the crash.
        }

        // Convert to LSP diagnostics
        var lspDiagnostics = bag
            .GetDiagnostics()
            .Select(ConvertDiagnostic)
            .ToArray();

        SendNotification("textDocument/publishDiagnostics", new
        {
            uri,
            diagnostics = lspDiagnostics,
        });
    }

    private static object ConvertDiagnostic(Diagnostic diag) => new
    {
        range = new
        {
            start = new
            {
                // LSP uses 0-based lines/columns; Culebral uses 1-based
                line = Math.Max(0, diag.Span.Start.Line - 1),
                character = Math.Max(0, diag.Span.Start.Column - 1),
            },
            end = new
            {
                line = Math.Max(0, diag.Span.End.Line - 1),
                character = Math.Max(0, diag.Span.End.Column - 1),
            },
        },
        severity = diag.Severity switch
        {
            DiagnosticSeverity.Error => 1,
            DiagnosticSeverity.Warning => 2,
            DiagnosticSeverity.Info => 3,
            _ => 4, // Hint
        },
        code = diag.Code,
        source = "culebral",
        message = diag.Message,
    };

    /// <summary>
    /// Converts a file:// URI to a local file path.
    /// </summary>
    private static string UriToFilePath(string uri)
    {
        if (uri.StartsWith("file:///", StringComparison.Ordinal))
        {
            // Unix: file:///home/user/file.leb -> /home/user/file.leb
            var path = Uri.UnescapeDataString(uri["file://".Length..]);
            return path;
        }

        if (uri.StartsWith("file://", StringComparison.Ordinal))
        {
            return Uri.UnescapeDataString(uri["file://".Length..]);
        }

        // Fallback: return as-is (might already be a path)
        return uri;
    }
}
