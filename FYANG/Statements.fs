module FYANG.Statements

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Text.RegularExpressions
open FParsec

/// Position in a file in terms of line and column.
[<StructuredFormatDisplay("{Line}:{Column}")>]
type Position = {
    Line: int64;
    Column: int64;
}

/// Generic statement.
type Statement(name: string, pos: Position) as this =
    member val Prefix: string option = None with get, set
    member val Name = name
    member val Argument: string option = None with get, set
    member val Children = StatementCollection(this)
    member val Parent: Statement option = None with get, set
    member val Position: Position = pos 

and StatementCollection(owner: Statement) =
    inherit Collection<Statement>()

    override this.InsertItem(index: int, stmt: Statement) =
        if stmt.Parent.IsSome then
            raise (ArgumentException "Statement already has a parent.")
        stmt.Parent <- Some(owner)
        base.InsertItem(index, stmt)

    override this.SetItem(index: int, stmt: Statement) =
        if stmt.Parent.IsSome then
            raise (ArgumentException "Statement already has a parent.")
        this.[index].Parent <- None
        stmt.Parent <- Some(owner)
        base.SetItem(index, stmt)

    override this.RemoveItem(index: int) =
        this.[index].Parent <- None
        base.RemoveItem(index)

    override this.ClearItems() =
        this |> Seq.iter (fun x -> x.Parent <- None)
        base.ClearItems()


// ---------------------------------------------------------------------------

// Whitespace and comments:
// comments are C-like ("//" for a line comment and "/*" + "*/" for a block comment).
// Note also that comments are considered as whitespace.

let singleSpaceChar = skipAnyOf " \t\n\r"
let lineComment = skipString "//" >>. skipRestOfLine true
let blockComment =
    between (skipString "/*") (skipString "*/")
            (skipCharsTillString "*/" false System.Int32.MaxValue)

let singleWhitespace = lineComment <|> blockComment <|> singleSpaceChar

// `ws` parses optional whitespace, `ws1` parses at least one whitespace,
// and fails if none has been found. Note that more consecutive whitespaces
// are parsed as one.
let ws: Parser<unit, unit> = skipMany singleWhitespace <?> "whitespace"
let ws1: Parser<unit, unit> = skipMany1 singleWhitespace <?> "whitespace"

// ---------------------------------------------------------------------------

// Helper function to strip whitespace from a multiline string literal.
// Strips whitespace at the end of each line and also
// skips initial spaces at the beginning of each line until the given column.
// Tab chars "\t" are counted as 8 spaces.
let trailingWhitespaceRegex = Regex("\\s*(\\r\\n?|\\n)", RegexOptions.Compiled)
let leadingWhitespaceRegex = Regex("\\n\\s*", RegexOptions.Compiled)
let stripWhitespaceFromStringLiteral str col =
    let s = trailingWhitespaceRegex.Replace(str, "\n")
    leadingWhitespaceRegex.Replace(s, (fun m ->
        // Checks that some space has been matched
        if m.Value.Length > 1 then
            
            // Count the total number of spaces: tabs count for 8,
            // and -1 is because the string contains a newline
            let spacesCount =
                m.Value.Length - 1 +
                7 * (m.Value |> Seq.fold (fun count c -> if c = '\t' then count + 1 else count) 0)
            
            // Replace all these spaces with the correct number
            if col > spacesCount then
                "\n"
            else
                "\n" + (String.replicate (spacesCount - col) " ")

        else
            m.Value
    ))

// Creates a parses for a literal string quoted with the given character.
// If `containsEscapes` is true, the stirng literal is allowed to have
// escaped chars in it, and the sequences of chars "\" "n", "\" "r" and "\" "t"
// will be parsed as a single char.
let quotedString quote containsEscapes : Parser<string, unit> =
    
    let normalChar =
        if containsEscapes then
            manySatisfy (fun c -> c <> quote && c <> '\\')
        else
            manySatisfy (fun c -> c <> quote)

    let escapedChar =
        pstring "\\" >>. (anyOf "\\\"nrt" |>> function
                                                | 'n' -> "\n"
                                                | 'r' -> "\r"
                                                | 't' -> "\t"
                                                | c -> string c)

    if containsEscapes then
        between (pchar quote) (pchar quote)
                (stringsSepBy normalChar escapedChar)
    else
        between (pchar quote) (pchar quote)
                normalChar

// String literal: can either be quoted or unquoted
let stringLiteral: Parser<string, unit> =

    // Unquoted strings end with a whitespace (comments included) or
    // one of the following characters: ";", "{", "}"
    let unquotedString =
        many1CharsTill
            (satisfy (isNoneOf ";{} \t\n\r"))
            (followedBy (ws1 <|> (skipSatisfy (isAnyOf ";{}")) <|> eof))
    
    // Quoted strings can be concatenated using the "+" operator
    let quotString = (quotedString '"' true) <|> (quotedString '\'' false)
    let multipleQuotedStrings =
        sepBy1 quotString (ws >>? pstring "+" .>>? ws)
        |>> String.concat ""

    let finalParser = multipleQuotedStrings <|> unquotedString

    // Returns a special parser that saves the column of the first char
    // and uses that to strip leading whitespace in multiline strings
    (fun stream ->
        let col = stream.Column
        let reply = finalParser stream
        if reply.Status <> Ok then
            reply
        else
            Reply(stripWhitespaceFromStringLiteral reply.Result (int col))
    )
    <??> "string"

// ---------------------------------------------------------------------------

// An identifier can start with a letter or an underscore,
// except X, M, or L
let idStart c = (isAsciiLetter c || c = '_') && c <> 'X' && c <> 'x' && c <> 'M' && c <> 'm' && c <> 'L' && c <> 'l'
let idContinue c = isAsciiLetter c || isDigit c || c = '_' || c = '-' || c = '.'
let idPreCheckStart c = isAsciiLetter c || c = '_'

let id : Parser<string, unit> =
    identifier (IdentifierOptions(isAsciiIdStart = idStart,
                                    isAsciiIdContinue = idContinue,
                                    preCheckStart = idPreCheckStart))
    <?> "identifier"

// ---------------------------------------------------------------------------

// Statements
let statement, statementRef = createParserForwardedToRef<Statement, unit> ()
statementRef :=
    getPosition
    .>>. opt (id .>>? pstring ":")        // Prefix
    .>>. id                          // Name
    .>>. opt (ws1 >>? stringLiteral) // Argument
    .>>  ws
    .>>. (
        (pstring ";" |>> fun _ -> [])                           // No sub-statements
        <|>
        (pstring "{" >>. ws >>. many statement .>> pstring "}") // Sub-statements
    )
    .>> ws
    <??> "statement"
    |>> fun ((((pos, prefix), name), arg), children) ->
            let s = Statement(
                        name,
                        { Line = pos.Line; Column = pos.Column },
                        Prefix = prefix,
                        Argument = arg
                    )
            children |> List.iter s.Children.Add
            s

// Entry rule
let root = ws >>. statement

// ---------------------------------------------------------------------------

// Other parsers used for argument parsing

let uintLength len : Parser<int, unit> =
    manyMinMaxSatisfy len len isDigit
    >>= (fun str ->
        match UInt32.TryParse(str) with
        | (true, i) -> preturn (int i)
        | _ -> fail "Invalid number"
    )

let dateArg: Parser<_, unit> =
    uintLength 4
    .>>  skipChar '-'
    .>>. uintLength 2
    .>>  skipChar '-'
    .>>. uintLength 2
    <?> "date"
    |>> (fun ((year, month), day) ->
        DateTime(year, month, day)
    )