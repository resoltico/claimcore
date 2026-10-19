namespace ClaimCore.Wire

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
        let anchored = "^(?:" + pattern + ")(?![\\s\\S])"
        let expression = Regex(anchored, RegexOptions.CultureInvariant)
        let schema = Schema.String(Some anchored, format, Some minimum, maximum)
        nonempty |> constrained schema expression.IsMatch

    let reference = grammar 1 (Some 80) "[!-~]{1,80}" None
    let country = grammar 2 (Some 2) "[A-Z]{2}" None
    let currency = grammar 3 (Some 3) "[A-Z]{3}" None
    let amount = grammar 1 (Some 23) "(0|[1-9][0-9]{0,17})(\\.[0-9]{1,4})?" None
    let digest = grammar 64 (Some 64) "[0-9a-f]{64}" None

    let date =
        grammar 10 (Some 10) "(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}" (Some "date")
        |> constrained
            (Schema.String(
                Some "^(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}(?![\\s\\S])",
                Some "date",
                Some 10,
                Some 10
            ))
            (fun value ->
                DateOnly.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                )
                |> fst)

    let businessField =
        tokens
            [
                "incidentDate"
                "incidentNotificationDate"
                "incidentCountry"
                "claimantName"
                "insurerName"
                "claimedAmount"
                "claimedCurrency"
                "caseReference"
                "paymentDecisionDate"
                "payableAmount"
                "payableCurrency"
                "paymentDate"
                "status"
            ]

    let capability =
        tokens
            [
                "READ_CASE"
                "READ_HISTORY"
                "READ_HISTORY_FULL"
                "REGISTER"
                "DECIDE"
                "RECORD_PAYMENT"
                "CLOSE"
                "STEWARD"
                "MANAGE_AUTHORITY"
                "READ_AUTHORITY"
            ]

    let fixedField name token (fields: Fields<'Whole, 'Ctor>) : Fields<'Whole, 'Ctor> =
        let codec = constant token

        { fields with
            ReadFields =
                fun path element ->
                    fields.ReadFields path element
                    |> Result.bind (fun ctor ->
                        match element.TryGetProperty(name: string) with
                        | true, value ->
                            codec.Read (path + "." + name) value |> Result.map (fun () -> ctor)
                        | _ ->
                            Error
                                {
                                    Path = path
                                    Reason = "missing member " + name
                                })
            WriteFields =
                fun writer value ->
                    fields.WriteFields writer value
                    writer.WritePropertyName(name: string)
                    codec.Write writer ()
            Properties = fields.Properties @ [ name, codec.Schema ]
        }
