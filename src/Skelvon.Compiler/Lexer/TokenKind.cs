namespace Skelvon.Compiler.Lexer;

/// <summary>
/// Every distinct token kind the Skelvon lexer can produce.
/// Organized by category for maintainability.
/// </summary>
public enum TokenKind
{
    // --- Structural ---
    Indent,
    Dedent,
    Newline,
    EndOfFile,

    // --- Literals ---
    IntegerLiteral,
    FloatLiteral,
    StringLiteral,
    FStringLiteral,
    BoolLiteral,      // true / false
    NoneLiteral,       // None

    // --- Identifiers ---
    Identifier,
    AtIdentifier,      // @field_name

    // --- Keywords ---
    KwDef,
    KwClass,
    KwStruct,
    KwRecord,
    KwEnum,
    KwInterface,
    KwProp,
    KwIf,
    KwElif,
    KwElse,
    KwFor,
    KwWhile,
    KwIn,
    KwReturn,
    KwYield,
    KwBreak,
    KwContinue,
    KwPass,
    KwImport,
    KwFrom,
    KwAs,
    KwWith,
    KwAsync,
    KwAwait,
    KwMatch,
    KwCase,
    KwAnd,
    KwOr,
    KwNot,
    KwIs,
    KwLambda,
    KwWhen,
    KwTarget,
    KwGet,
    KwSet,
    KwVia,
    KwModule,
    KwNative,
    KwType,
    KwTry,
    KwExcept,
    KwFinally,
    KwRaise,

    // --- Operators ---
    Plus,              // +
    Minus,             // -
    Star,              // *
    DoubleStar,        // **
    Slash,             // /
    DoubleSlash,       // //
    Percent,           // %
    Ampersand,         // &
    Pipe,              // |
    Caret,             // ^
    Tilde,             // ~
    ShiftLeft,         // <<
    ShiftRight,        // >>

    // --- Comparison ---
    Equal,             // ==
    NotEqual,          // !=
    LessThan,          // <
    GreaterThan,       // >
    LessEqual,         // <=
    GreaterEqual,      // >=

    // --- Assignment ---
    Assign,            // =
    PlusAssign,        // +=
    MinusAssign,       // -=
    StarAssign,        // *=
    SlashAssign,       // /=
    PercentAssign,     // %=
    DoubleSlashAssign, // //=
    DoubleStarAssign,  // **=
    AmpersandAssign,   // &=
    PipeAssign,        // |=
    CaretAssign,       // ^=
    ShiftLeftAssign,   // <<=
    ShiftRightAssign,  // >>=

    // --- Delimiters ---
    LeftParen,         // (
    RightParen,        // )
    LeftBracket,       // [
    RightBracket,      // ]
    LeftBrace,         // {
    RightBrace,        // }
    Comma,             // ,
    Colon,             // :
    Semicolon,         // ;
    Dot,               // .
    Arrow,             // ->
    Question,          // ?
    At,                // @ (decorator)
    Underscore,        // _ (wildcard)
    Ellipsis,          // ...
}
