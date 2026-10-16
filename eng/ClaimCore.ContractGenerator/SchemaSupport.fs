namespace ClaimCore.ContractGeneration

open ClaimCore.ClientProtocol
open System
open System.Text.RegularExpressions

/// Generation refuses schema primitives that either client runtime cannot interpret.
module SchemaSupport =
    let private matches (pattern: string) (value: string) =
        Regex.IsMatch(
            value,
            pattern,
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(200.)
        )

    let private unsupportedRepetition (value: string) =
        value.Contains('*')
        || value.Contains('+')
        || value.Contains("){", StringComparison.Ordinal)
        || matches @"\{[0-9]+,\}" value
        || matches @"[^\]]\{[0-9]" value

    let private pattern (value: string) =
        let remaining = value.Replace(@"(?![\s\S])", "")
        let literals = remaining.Replace(@"\.", "").Replace(@"\+", "")

        if
            matches @"\\(?![.+])" remaining
            || matches @"\(\?(?!!|:)" remaining
            || unsupportedRepetition literals
            || literals.Contains(".", StringComparison.Ordinal)
            || matches @"\[[^\]]*-\[" remaining
            || remaining.Contains("[^", StringComparison.Ordinal)
            || (remaining |> Seq.exists (fun character -> int character > 127))
        then
            invalidArg "schema" "Unsupported client regular-expression grammar."

    let private safeInteger value =
        value >= -9007199254740991L && value <= 9007199254740991L

    let private integer value =
        match value.Minimum, value.Maximum with
        | Some minimum, Some maximum when
            safeInteger minimum && safeInteger maximum && minimum <= maximum
            ->
            ()
        | _ -> invalidArg "schema" "Client integers require explicit safe bounds."

    let private constant value =
        match value with
        | IntegerConstant number when not (safeInteger number) ->
            invalidArg "schema" "Client integer constants must be safe integers."
        | _ -> ()

    let private string value =
        value.Pattern |> Option.iter pattern

        match value.Format with
        | None
        | Some "uuid"
        | Some "date" -> ()
        | Some "date-time" when value.Pattern = Some ProtocolProfile.CanonicalTimestampPattern -> ()
        | _ -> invalidArg "schema" "Unsupported client string format."

    let rec private validate schema =
        match schema with
        | StringSchema value -> string value
        | IntegerSchema value -> integer value
        | ConstantSchema value -> constant value
        | EnumerationSchema values -> values |> List.iter constant
        | ArraySchema value -> validate value.Item
        | TupleSchema items
        | OneOfSchema items -> items |> List.iter validate
        | ObjectSchema value ->
            value.Properties |> List.iter (fun property -> validate property.Schema)
        | DictionarySchema value -> validate value
        | _ -> ()

    let document value =
        validate value.Root
        value.Definitions |> List.iter (snd >> validate)
