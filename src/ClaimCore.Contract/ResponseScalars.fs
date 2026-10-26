namespace ClaimCore.Contract.Protocol

open ClaimCore.Domain

open ClaimCore.Contract
open ClaimCore.Json

open System
open System.Globalization
open System.Text.RegularExpressions

/// Permanent response grammars describe accepted values, independently of current admission policy.
module ResponseScalars =
    open Codec

    let revision = RequestCodecs.revision
    let sequence = RequestCodecs.sequence
    let tokens = RequestCodecs.tokens
    let nonempty = boundedText 1 Int32.MaxValue

    let private grammar minimum maximum pattern format =
        let anchored = Formats.anchored pattern
        let expression = Regex(anchored, RegexOptions.CultureInvariant)
        let schema = Schema.String(Some anchored, format, Some minimum, maximum)
        nonempty |> constrained schema expression.IsMatch

    let reference =
        grammar 1 (Some Formats.ReferenceMaximumCharacters) Formats.reference None

    let country =
        grammar Formats.CountryCharacters (Some Formats.CountryCharacters) Formats.country None

    let currency =
        grammar Formats.CurrencyCharacters (Some Formats.CurrencyCharacters) Formats.currency None

    let amount = grammar 1 (Some Formats.amountMaximumCharacters) Formats.amount None
    let digest = grammar 64 (Some 64) "[0-9a-f]{64}" None

    let date =
        grammar Formats.dateCharacters (Some Formats.dateCharacters) Formats.date (Some "date")
        |> constrained
            (Schema.String(
                Some("^" + Formats.date + "(?![\\s\\S])"),
                Some "date",
                Some Formats.dateCharacters,
                Some Formats.dateCharacters
            ))
            (fun value ->
                DateOnly.TryParseExact(
                    value,
                    Formats.CalendarDateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                )
                |> fst)

    let businessField = Vocabulary.tokens FieldTable.vocabulary |> tokens

    let capability = Capabilities.all |> List.map Capabilities.token |> tokens

    let fixedField name token (fields: Fields<'Whole, 'Ctor>) : Fields<'Whole, 'Ctor> =
        let codec = constant token

        { fields with
            ReadFields =
                fun path element ->
                    fields.ReadFields path element
                    |> Result.bind (fun ctor ->
                        match JsonValue.tryMember name element with
                        | Some value ->
                            codec.Read (path + "." + name) value |> Result.map (fun () -> ctor)
                        | _ ->
                            Error
                                {
                                    Path = path
                                    Reason = "missing member " + name
                                })
            WriteFields = fun value -> fields.WriteFields value @ [ name, codec.Write() ]
            Properties = fields.Properties @ [ name, codec.Schema ]
        }
