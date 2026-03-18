using Skelvon.Compiler.Diagnostics;
using Skelvon.Compiler.Lexer;

namespace Skelvon.Compiler.Tests;

public class LexerTests
{
    private static List<Token> Lex(string source)
    {
        var diagnostics = new DiagnosticBag();
        var lexer = new SkelvonLexer(source, "<test>", diagnostics);
        var tokens = lexer.Tokenize();
        Assert.False(diagnostics.HasErrors, diagnostics.FormatAll());
        return tokens;
    }

    private static List<Token> LexWithErrors(string source, out DiagnosticBag diagnostics)
    {
        diagnostics = new DiagnosticBag();
        var lexer = new SkelvonLexer(source, "<test>", diagnostics);
        return lexer.Tokenize();
    }

    [Fact]
    public void EmptySource_ProducesOnlyEof()
    {
        var tokens = Lex("");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.EndOfFile, tokens[0].Kind);
    }

    [Fact]
    public void HelloWorld_ProducesCorrectTokens()
    {
        var tokens = Lex("def main():\n    print(\"Hello\")\n");
        var kinds = tokens.Select(t => t.Kind).ToArray();

        Assert.Equal(TokenKind.KwDef, kinds[0]);
        Assert.Equal(TokenKind.Identifier, kinds[1]);
        Assert.Equal(TokenKind.LeftParen, kinds[2]);
        Assert.Equal(TokenKind.RightParen, kinds[3]);
        Assert.Equal(TokenKind.Colon, kinds[4]);
        Assert.Equal(TokenKind.Newline, kinds[5]);
        Assert.Equal(TokenKind.Indent, kinds[6]);
        Assert.Equal(TokenKind.Identifier, kinds[7]); // print
        Assert.Equal(TokenKind.LeftParen, kinds[8]);
        Assert.Equal(TokenKind.StringLiteral, kinds[9]);
        Assert.Equal(TokenKind.RightParen, kinds[10]);
    }

    [Fact]
    public void IntegerLiterals_ParseCorrectly()
    {
        var tokens = Lex("42 0xFF 0b1010 0o17 1_000_000");

        Assert.Equal(TokenKind.IntegerLiteral, tokens[0].Kind);
        Assert.Equal(42L, tokens[0].LiteralValue);

        Assert.Equal(TokenKind.IntegerLiteral, tokens[1].Kind);
        Assert.Equal(255L, tokens[1].LiteralValue);

        Assert.Equal(TokenKind.IntegerLiteral, tokens[2].Kind);
        Assert.Equal(10L, tokens[2].LiteralValue);

        Assert.Equal(TokenKind.IntegerLiteral, tokens[3].Kind);
        Assert.Equal(15L, tokens[3].LiteralValue);

        Assert.Equal(TokenKind.IntegerLiteral, tokens[4].Kind);
        Assert.Equal(1000000L, tokens[4].LiteralValue);
    }

    [Fact]
    public void FloatLiterals_ParseCorrectly()
    {
        var tokens = Lex("3.14 1e10 2.5e-3");

        Assert.Equal(TokenKind.FloatLiteral, tokens[0].Kind);
        Assert.Equal(3.14, tokens[0].LiteralValue);

        Assert.Equal(TokenKind.FloatLiteral, tokens[1].Kind);
        Assert.Equal(1e10, tokens[1].LiteralValue);

        Assert.Equal(TokenKind.FloatLiteral, tokens[2].Kind);
        Assert.Equal(2.5e-3, tokens[2].LiteralValue);
    }

    [Fact]
    public void StringLiterals_HandleEscapes()
    {
        var tokens = Lex("\"hello\\nworld\"");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("hello\nworld", tokens[0].Lexeme);
    }

    [Fact]
    public void Keywords_AreRecognized()
    {
        var tokens = Lex("def class struct record enum interface if elif else for while in return");
        var kinds = tokens.Select(t => t.Kind).TakeWhile(k => k != TokenKind.EndOfFile).ToList();

        Assert.Contains(TokenKind.KwDef, kinds);
        Assert.Contains(TokenKind.KwClass, kinds);
        Assert.Contains(TokenKind.KwStruct, kinds);
        Assert.Contains(TokenKind.KwRecord, kinds);
        Assert.Contains(TokenKind.KwEnum, kinds);
        Assert.Contains(TokenKind.KwInterface, kinds);
        Assert.Contains(TokenKind.KwIf, kinds);
        Assert.Contains(TokenKind.KwElif, kinds);
        Assert.Contains(TokenKind.KwElse, kinds);
        Assert.Contains(TokenKind.KwFor, kinds);
        Assert.Contains(TokenKind.KwWhile, kinds);
        Assert.Contains(TokenKind.KwIn, kinds);
        Assert.Contains(TokenKind.KwReturn, kinds);
    }

    [Fact]
    public void BoolAndNone_AreRecognized()
    {
        var tokens = Lex("True False None");
        Assert.Equal(TokenKind.BoolLiteral, tokens[0].Kind);
        Assert.Equal(true, tokens[0].LiteralValue);
        Assert.Equal(TokenKind.BoolLiteral, tokens[1].Kind);
        Assert.Equal(false, tokens[1].LiteralValue);
        Assert.Equal(TokenKind.NoneLiteral, tokens[2].Kind);
    }

    [Fact]
    public void Operators_AreTokenized()
    {
        var tokens = Lex("+ - * ** / // % == != <= >= -> +=");
        var kinds = tokens.Select(t => t.Kind).TakeWhile(k => k != TokenKind.EndOfFile).ToList();

        Assert.Contains(TokenKind.Plus, kinds);
        Assert.Contains(TokenKind.Minus, kinds);
        Assert.Contains(TokenKind.Star, kinds);
        Assert.Contains(TokenKind.DoubleStar, kinds);
        Assert.Contains(TokenKind.Slash, kinds);
        Assert.Contains(TokenKind.DoubleSlash, kinds);
        Assert.Contains(TokenKind.Percent, kinds);
        Assert.Contains(TokenKind.Equal, kinds);
        Assert.Contains(TokenKind.NotEqual, kinds);
        Assert.Contains(TokenKind.LessEqual, kinds);
        Assert.Contains(TokenKind.GreaterEqual, kinds);
        Assert.Contains(TokenKind.Arrow, kinds);
        Assert.Contains(TokenKind.PlusAssign, kinds);
    }

    [Fact]
    public void IndentDedent_NestedBlocks()
    {
        var source = "if x:\n    if y:\n        pass\n    pass\n";
        var tokens = Lex(source);
        var kinds = tokens.Select(t => t.Kind).ToList();

        // Should have two INDENTs and two DEDENTs
        Assert.Equal(2, kinds.Count(k => k == TokenKind.Indent));
        Assert.Equal(2, kinds.Count(k => k == TokenKind.Dedent));
    }

    [Fact]
    public void BracketsSuppress_Newlines()
    {
        var source = "x = [\n    1,\n    2,\n    3\n]\n";
        var tokens = Lex(source);

        // No INDENT/DEDENT inside brackets
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Indent);
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Dedent);
    }

    [Fact]
    public void FString_IsLexed()
    {
        var tokens = Lex("f\"Hello, {name}!\"");
        Assert.Equal(TokenKind.FStringLiteral, tokens[0].Kind);
        Assert.Equal("Hello, {name}!", tokens[0].Lexeme);
    }

    [Fact]
    public void AtIdentifier_IsLexed()
    {
        var tokens = Lex("@count");
        Assert.Equal(TokenKind.AtIdentifier, tokens[0].Kind);
        Assert.Equal("@count", tokens[0].Lexeme);
    }

    [Fact]
    public void Comments_AreSkipped()
    {
        var tokens = Lex("x = 1  # this is a comment\ny = 2\n");
        // Should not contain any comment tokens
        Assert.DoesNotContain(tokens, t => t.Lexeme.Contains("#"));
    }

    [Fact]
    public void Ellipsis_IsLexed()
    {
        var tokens = Lex("...");
        Assert.Equal(TokenKind.Ellipsis, tokens[0].Kind);
    }

    [Fact]
    public void UnterminatedString_ReportsError()
    {
        var tokens = LexWithErrors("\"unterminated", out var diagnostics);
        Assert.True(diagnostics.HasErrors);
    }
}
