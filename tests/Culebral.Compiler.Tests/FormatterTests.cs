namespace Culebral.Compiler.Tests;

public class FormatterTests
{
    [Fact]
    public void FormatSource_StripTrailingWhitespace()
    {
        var input = "x = 1   \ny = 2  \n";
        var result = Program.FormatSource(input);
        Assert.Equal("x = 1\ny = 2\n", result);
    }

    [Fact]
    public void FormatSource_CollapseBlankLines()
    {
        var input = "x = 1\n\n\n\n\ny = 2\n";
        var result = Program.FormatSource(input);
        Assert.Equal("x = 1\n\n\ny = 2\n", result);
    }

    [Fact]
    public void FormatSource_TwoBlankLinesBeforeDef()
    {
        var input = "x = 1\ndef foo():\n    pass\n";
        var result = Program.FormatSource(input);
        Assert.Equal("x = 1\n\n\ndef foo():\n    pass\n", result);
    }

    [Fact]
    public void FormatSource_TwoBlankLinesBeforeClass()
    {
        var input = "x = 1\nclass Foo:\n    pass\n";
        var result = Program.FormatSource(input);
        Assert.Equal("x = 1\n\n\nclass Foo:\n    pass\n", result);
    }

    [Fact]
    public void FormatSource_TwoBlankLinesBeforeAsyncDef()
    {
        var input = "x = 1\nasync def foo():\n    pass\n";
        var result = Program.FormatSource(input);
        Assert.Equal("x = 1\n\n\nasync def foo():\n    pass\n", result);
    }

    [Fact]
    public void FormatSource_NoExtraBlanksBeforeIndentedDef()
    {
        // Indented def should NOT get 2 blank lines inserted
        var input = "class Foo:\n    def bar():\n        pass\n";
        var result = Program.FormatSource(input);
        Assert.Equal("class Foo:\n    def bar():\n        pass\n", result);
    }

    [Fact]
    public void FormatSource_EnsureSingleTrailingNewline()
    {
        var input = "x = 1\n\n\n";
        var result = Program.FormatSource(input);
        Assert.Equal("x = 1\n", result);
    }

    [Fact]
    public void FormatSource_AddsTrailingNewlineIfMissing()
    {
        var input = "x = 1";
        var result = Program.FormatSource(input);
        Assert.Equal("x = 1\n", result);
    }

    [Fact]
    public void FormatSource_AlreadyFormatted_NoChange()
    {
        var input = "x = 1\n\n\ndef foo():\n    pass\n";
        var result = Program.FormatSource(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void FormatSource_StripCarriageReturn()
    {
        var input = "x = 1\r\ny = 2\r\n";
        var result = Program.FormatSource(input);
        Assert.Equal("x = 1\ny = 2\n", result);
    }

    [Fact]
    public void FormatSource_EmptyInput_ReturnsNewline()
    {
        var input = "";
        var result = Program.FormatSource(input);
        Assert.Equal("\n", result);
    }

    [Fact]
    public void FormatSource_FirstDefNoExtraBlanks()
    {
        // def at the very start should not get blank lines before it
        var input = "def main():\n    pass\n";
        var result = Program.FormatSource(input);
        Assert.Equal("def main():\n    pass\n", result);
    }

    [Fact]
    public void FormatSource_MultipleDefs_TwoBlanksEach()
    {
        var input = "def foo():\n    pass\ndef bar():\n    pass\n";
        var result = Program.FormatSource(input);
        Assert.Equal("def foo():\n    pass\n\n\ndef bar():\n    pass\n", result);
    }

    [Fact]
    public void FormatSource_StructAndEnum()
    {
        var input = "x = 1\nstruct Point:\n    x: int\nenum Color:\n    Red\n";
        var result = Program.FormatSource(input);
        Assert.Equal("x = 1\n\n\nstruct Point:\n    x: int\n\n\nenum Color:\n    Red\n", result);
    }
}
