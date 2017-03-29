module FYANG.Model

open System
open System.Collections.Generic
open System.Globalization
open System.Text.RegularExpressions
open FYANG.Statements


/// Base class for all YANG nodes
[<AbstractClass>]
[<AllowNullLiteral>]
type YANGNode() =
    member val OriginalStatement: Statement option = None with get, set


/// Base class for a YANG node that carries actual data.
[<AbstractClass>]
type YANGDataNode() =
    inherit YANGNode()


/// Status of a YANG schema node.
/// The default status for all nodes is `Current`.
/// Note that a node with status `Current` cannot reference any node with status
/// `Deprecated` or `Obsolete`, as well as a `Deprecated` node cannot reference
/// an `Obsolete` node.
type YANGStatus =
    | Current = 1
    | Deprecated = 2
    | Obsolete = 3


/// Representation of all the possible errors that can happen
/// during the parsing and validation of a YANG model
[<StructuredFormatDisplay("{Text}")>]
type SchemaError =
    
    // Generic syntax error
    | ParserError of string
    
    // YANG Version
    | UnsupportedYangVersion of Statement * string
    
    // Statement argument
    | ArgumentExpected of Statement
    | NoArgumentExpected of Statement
    | ArgumentParserError of Statement * string
    
    // Statement cardinality
    | ExpectedStatement of Statement * string
    | UnexpectedStatement of Statement
    | MissingRequiredStatement of Statement * string
    | TooManyInstancesOfStatement of Statement

    // Namespaces and modules
    | AlreadyUsedModuleName of Statement * YANGModule
    | UnknownPrefix of Statement * string
    | AlreadyUsedPrefix of Statement * Statement
    | AlreadyUsedNamespace of Statement * YANGModule

    // Types
    | ShadowedType of Statement * YANGType
    | InvalidDefault of YANGType * YANGTypeRestriction
    | UnresolvedTypeRef of Statement * string

    with

        member this.Text = this.ToString()

        override this.ToString() =
            let (stmt, msg) =
                match this with
                | ParserError(x) ->
                    None, x
                | UnsupportedYangVersion(x, y) ->
                    Some x, (sprintf "Unsupported YANG version %s." y)
                | ArgumentExpected(x) ->
                    Some x, (sprintf "Statement \"%s\" expects an argument." x.Name)
                | NoArgumentExpected(x) ->
                    Some x, (sprintf "Statement \"%s\" does not expect an argument." x.Name)
                | ArgumentParserError(x, y) ->
                    Some x, (sprintf "Error parsing argument for statement \"%s\":%s%s" x.Name Environment.NewLine y)
                | ExpectedStatement(x, y) ->
                    Some x, (sprintf "Expected \"%s\" statement, but got \"%s\"." x.Name y)
                | UnexpectedStatement(x) ->
                    Some x, (sprintf "Unexpected statement \"%s\"." x.Name)
                | MissingRequiredStatement(x, y) ->
                    Some x, (sprintf "Missing required statement \"%s\"." y)
                | TooManyInstancesOfStatement(x) ->
                    Some x, (sprintf "Too many instances of the \"%s\" statement." x.Name)
                | AlreadyUsedModuleName(x, y) ->
                    let stmt: Statement option = y.OriginalStatement
                    match stmt with
                    | Some(s) -> Some x, (sprintf "Module name already used by module at %A." s.Position)
                    | None -> Some x, "Module name already used."
                | UnknownPrefix(x, y) ->
                    Some x, (sprintf "Unknown prefix \"%s\"." y)
                | AlreadyUsedPrefix(x, y) ->
                    Some x, (sprintf "Prefix already registered. See statement at %d:%d" y.Position.Line y.Position.Column)
                | AlreadyUsedNamespace(x, y) ->
                    let stmt: Statement option = y.OriginalStatement
                    match stmt with
                    | Some(s) -> Some x, (sprintf "Namespace already registered by module \"%A\" (%A)." y.Name s.Position)
                    | None -> Some x, (sprintf "Namespace already registered by module \"%A\"." y.Name)
                | ShadowedType(x, y) ->
                    let stmt: Statement option = y.OriginalStatement
                    match stmt with
                    | Some(s) -> Some x, (sprintf "This type shadows the type %A defined at %A." y.Name s.Position)
                    | None -> Some x, "This type shadows a type defined in an higher scope."
                | InvalidDefault(x, y) ->
                    let restriction =
                        match y.OriginalStatement with
                        | Some(s) -> s.ToString()
                        | None -> "<position not available>"
                    x.OriginalStatement, (sprintf "This type has an invalid default value. See restriction at %s." restriction)
                | UnresolvedTypeRef(x, y) ->
                    Some x, (sprintf "Cannot find type %s." y)
            
            match stmt with
            | Some(s) -> sprintf "Statement \"%s\" (%A): %s" s.Name s.Position msg
            | None -> msg


/// Namespace used in a YANG model.
/// Namespaces are strictly tied to the module that defined them.
and [<StructuredFormatDisplay("{{{Uri}}}")>] YANGNamespace =
    {
        Module: YANGModule;
        Uri: Uri;
    }
    with
        static member Default = { Module = null; Uri = Uri("urn:ietf:params:xml:ns:yang:1") }
        static member Invalid = { Module = null; Uri = null }


/// Name of YANGNode.
/// Names can't be simple strings because they need to be qualified by a namespace.
and [<StructuredFormatDisplay("{Namespace}{Name}")>] YANGName =
    {
        Namespace: YANGNamespace;
        Name: string;
    }



// -------------------------------------------------------------------
// Implementation of all the supported YANG data types
// -------------------------------------------------------------------

/// Restriction on the value of a YANG type.
and [<AbstractClass>] YANGTypeRestriction() =
    inherit YANGNode()

    member val ErrorMessage: string = null with get, set
    member val ErrorAppTag: string = null with get, set
    member val Description: string = null with get, set
    member val Reference: string = null with get, set

    abstract member IsValid: obj -> bool


/// Restriction on the value of a numeral type.
and YANGRangeRestriction(ranges: Statements.Range<float> list) =
    inherit YANGTypeRestriction()

    override this.IsValid o =
        if isNull o then
            false
        else
            try
                let n = Convert.ToDouble(o)
                ranges
                |> List.exists (fun range ->
                    n >= range.Min && n <= range.Max
                )
            with
            | _ -> false


/// Restriction on the length of a string or binary data.
and YANGLengthRestriction(ranges: Statements.Range<uint32> list) =
    inherit YANGTypeRestriction()

    override __.IsValid o =
        let len =
            if isNull o then
                None
            else if o :? string then
                let str = o :?> string
                Some(uint32 str.Length)
            else if o :? byte[] then
                let arr = o :?> byte[]
                Some(uint32 arr.Length)
            else
                None
        match len with
        | None -> false
        | Some(n) ->
            ranges
            |> List.exists (fun range ->
                n >= range.Min && n <= range.Max
            )


/// Restricts a string value to a given pattern.
and YANGPatternRestriction(r: Regex) =
    inherit YANGTypeRestriction()

    member __.Pattern = r

    override __.IsValid o =
        if not (isNull o) && o :? string then
            r.IsMatch(o :?> string)
        else
            false


/// Base class for all YANG data types.
/// The properties `Default`, `Description`, `Reference`, `Status` and `Units`
/// inherit thei values from the `BaseType` if not set manually.
and YANGType(name: YANGName) =
    inherit YANGNode()

    let mutable _default: obj option = None
    let mutable _description: string option = None
    let mutable _reference: string option = None
    let mutable _status: YANGStatus option = None
    let mutable _units: string option = None

    member __.Name = name
    
    member val BaseType: YANGType option = None with get, set
    
    member this.Default
        with get() =
            match _default, this.BaseType with
            | None, Some b -> b.Default
            | None, None -> null
            | Some v, _ -> v
        and set(v) =
            _default <- Some v

    member this.Description
        with get() =
            match _description, this.BaseType with
            | None, Some b -> b.Description
            | None, None -> null
            | Some v, _ -> v
        and set(v) =
            _description <- Some v
    
    member this.Reference
        with get() =
            match _reference, this.BaseType with
            | None, Some b -> b.Reference
            | None, None -> null
            | Some v, _ -> v
        and set(v) =
            _reference <- Some v
    
    member this.Status
        with get() =
            match _status, this.BaseType with
            | None, Some b -> b.Status
            | None, None -> YANGStatus.Current
            | Some v, _ -> v
        and set(v) =
            _status <- Some v
    
    member this.Units
        with get() =
            match _units, this.BaseType with
            | None, Some b -> b.Units
            | None, None -> null
            | Some v, _ -> v
        and set(v) =
            _units <- Some v

    member val Restrictions = ResizeArray<YANGTypeRestriction>()

    member this.IsValid o =
        let violatedRestriction =
            this.Restrictions
            |> Seq.tryFind (fun r -> not (r.IsValid o))
        match violatedRestriction, this.BaseType with
        | Some r, _ -> Error r
        | None, Some b -> b.IsValid o
        | None, None -> Ok()
    
    abstract member Parse: string -> obj option
    default this.Parse (str: string) : obj option =
        match this.BaseType with
        | Some(b) -> b.Parse str
        | None -> invalidOp (sprintf "Missing parsing logic for type %A" this.Name)

    abstract member Serialize: obj -> string option
    default this.Serialize (o: obj) : string option =
        match this.BaseType with
        | Some(b) -> b.Serialize o
        | None -> invalidOp (sprintf "Missing serializing logic for type %A" this.Name)


/// The `decimal64` YANG type.
and YANGDecimal64Type() =
    inherit YANGType({ Namespace = YANGNamespace.Default; Name = "decimal64" })
    
    let mutable _digits = 0
    let mutable _min = 0.0
    let mutable _max = 0.0
    let mutable _formatString: string = null;

    member this.SetFractionDigits n =
        if n >= 1 && n <= 18 then
            _digits <- n
            _min <- (float Int64.MinValue) / (10.0 ** float n)
            _max <- (float Int64.MaxValue) / (10.0 ** float n)
            _formatString <- sprintf "{0:0.%s}" (String('0', n))
            Ok()
        else
            Error()

    override this.Parse str =
        if _digits = 0 then
            invalidOp "Cannot call `Parse` before setting fraction digits."

        if isNull str then
            None
        else
        
            // A decimal64 number is just a signed 64 bit integer with a decimal point in the middle.
            // This means that we can parse it like any other ordinary floating point number,
            // and then we just check that it is between Int64.MinValue * 10^(-digits)
            // and Int64.MaxValue * 10^(-digits).
            // We also need to check that contains at least one decimal digit, since integers are not allowed,
            // and TryParse would parse them.

            let dotIndex = str.IndexOf('.')
            if dotIndex < 1 || dotIndex > str.Length - 2 then
                None
            else

                let numberStyle =
                    NumberStyles.AllowDecimalPoint |||
                    NumberStyles.AllowLeadingWhite |||
                    NumberStyles.AllowTrailingWhite |||
                    NumberStyles.AllowLeadingSign
                match System.Double.TryParse(str, numberStyle, NumberFormatInfo.InvariantInfo) with
                | (true, n) ->
                    if n >= _min && n <= _max then
                        Some(box n)
                    else
                        None
                | _ -> None


    override this.Serialize o =
        if _digits = 0 then
            invalidOp "Cannot call `Serialize` before setting fraction digits."
        
        if o :? float then
            let n = o :?> float
            if n < _min || n > _max then
                None
            else
                Some (n.ToString _formatString)
        else
            None


/// Static class with all the primitive types defined by YANG.
and YANGPrimitiveTypes private () =
    
    static member private MakeIntegralType<'T> name (tryParse: string -> bool * 'T) =

        {
            new YANGType({ Namespace = YANGNamespace.Default; Name = name }) with

                override this.Parse str =
                    if isNull str then
                        None
                    else
                        match tryParse str with
                        | (true, obj) -> Some(box obj)
                        | _ -> None

                override this.Serialize o =
                    if isNull o || not (o :? 'T)then
                        None
                    else
                        Some(o.ToString())

        }

    static member Empty = {
        new YANGType({ Namespace = YANGNamespace.Default; Name = "empty" }) with

            override this.Parse _ =
                None

            override this.Serialize _ =
                None

    }

    static member Boolean = {
        new YANGType({ Namespace = YANGNamespace.Default; Name = "boolean" }) with

            override this.Parse str =
                if isNull str then
                    None
                else if str = "true" then
                    Some(box true)
                else if str = "false" then
                    Some(box false)
                else
                    None

            override this.Serialize o =
                if isNull o || not (o :? bool) then
                    None
                else
                    Some(if o :?> bool then "true" else "false")

    }

    static member Int8 = YANGPrimitiveTypes.MakeIntegralType "int8" System.SByte.TryParse
    static member Int16 = YANGPrimitiveTypes.MakeIntegralType "int16" System.Int16.TryParse
    static member Int32 = YANGPrimitiveTypes.MakeIntegralType "int32" System.Int32.TryParse
    static member Int64 = YANGPrimitiveTypes.MakeIntegralType "int64" System.Int64.TryParse
    static member UInt8 = YANGPrimitiveTypes.MakeIntegralType "uint8" System.Byte.TryParse
    static member UInt16 = YANGPrimitiveTypes.MakeIntegralType "uint16" System.UInt16.TryParse
    static member UInt32 = YANGPrimitiveTypes.MakeIntegralType "uint32" System.UInt32.TryParse
    static member UInt64 = YANGPrimitiveTypes.MakeIntegralType "uint64" System.UInt64.TryParse

    static member String = {
        new YANGType({ Namespace = YANGNamespace.Default; Name = "string" }) with

            override this.Parse str =
                if isNull str then
                    None
                else
                    Some(str :> obj)

            override this.Serialize o =
                if isNull o || not (o :? string) then
                    None
                else
                    Some(o :?> string)

    }

    static member Binary = {
        new YANGType({ Namespace = YANGNamespace.Default; Name = "string" }) with

            override this.Parse str =
                if isNull str then 
                    None
                else
                    try
                        Some(Convert.FromBase64String(str) :> obj)
                    with
                    | :? FormatException -> None

            override this.Serialize o =
                if isNull o || not (o :? byte[]) then
                    None
                else
                    Some(Convert.ToBase64String(o :?> byte[]))

    }

    static member FromName(name: string) =
        match name with
        | "empty" -> Some(YANGPrimitiveTypes.Empty)
        | "boolean" -> Some(YANGPrimitiveTypes.Boolean)
        | "int8" -> Some(YANGPrimitiveTypes.Int8)
        | "int16" -> Some(YANGPrimitiveTypes.Int16)
        | "int32" -> Some(YANGPrimitiveTypes.Int32)
        | "int64" -> Some(YANGPrimitiveTypes.Int64)
        | "uint8" -> Some(YANGPrimitiveTypes.UInt8)
        | "uint16" -> Some(YANGPrimitiveTypes.UInt16)
        | "uint32" -> Some(YANGPrimitiveTypes.UInt32)
        | "uint64" -> Some(YANGPrimitiveTypes.UInt64)
        | "string" -> Some(YANGPrimitiveTypes.String)
        | "binary" -> Some(YANGPrimitiveTypes.Binary)
        | _ -> None
        



// -------------------------------------------------------------------
// Implementation of all the supported nodes
// -------------------------------------------------------------------

and [<AllowNullLiteral>] YANGModule(unqualifiedName: string) =
    inherit YANGNode()
    
    member this.Name
        with get() =
            if this.Namespace = YANGNamespace.Invalid then
                invalidOp "Cannot retrive fully qualified name before setting the `Namespace` property."
            {
                Namespace = this.Namespace;
                Name = unqualifiedName;
            }

    member val Namespace = YANGNamespace.Invalid with get, set
    member val Prefix: string = null with get, set
    member val Contact: string = null with get, set
    member val Organization: string = null with get, set
    member val Description: string = null with get, set
    member val Reference: string = null with get, set

    member val Revisions = ResizeArray<YANGModuleRevision>()

    member val DataNodes = ResizeArray<YANGDataNode>()

    member val ExportedTypes = Dictionary<YANGName, YANGType>()


and YANGModuleRevision(date: DateTime) =
    inherit YANGNode()

    member val Date = date
    member val Description: string = null with get, set
    member val Reference: string = null with get, set


type YANGContainer(name: YANGName) =
    inherit YANGDataNode()

    member this.Name = name
    

type YANGLeaf(name: YANGName) =
    inherit YANGDataNode()

    member this.Name = name


type YANGLeafList(name: YANGName) =
    inherit YANGDataNode()

    member this.Name = name


type YANGList(name: YANGName) =
    inherit YANGDataNode()

    member this.Name = name