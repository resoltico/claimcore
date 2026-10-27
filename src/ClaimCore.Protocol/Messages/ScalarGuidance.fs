namespace ClaimCore.Protocol

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type GuidanceArgument =
    | Count of int
    | Code of string

type ScalarGuidance =
    {
        Key: string
        Arguments: Map<string, GuidanceArgument>
    }

/// Guidance combines core wording with Domain-owned scalar bounds; hosts only format arguments.
module ScalarGuidance =
    let private message key arguments =
        {
            Key = key
            Arguments = Map.ofList arguments
        }

    let private maximum descriptor =
        descriptor.MaximumCharacters
        |> Option.defaultWith (fun () ->
            invalidOp "Text guidance requires its Domain admission bound.")

    let forField field =
        match BusinessField.valueKind field with
        | FieldValueKind.CalendarDate -> Some(message "scalar.calendarDate.guidance" [])
        | FieldValueKind.Amount ->
            Some(
                message
                    "scalar.amount.guidance"
                    [
                        "maximumFractionDigits",
                        GuidanceArgument.Count Money.descriptor.Amount.MaximumFractionalDigits
                    ]
            )
        | FieldValueKind.Currency ->
            Some(
                message
                    "scalar.currency.guidance"
                    [ "length", GuidanceArgument.Count Money.descriptor.Currency.ExactCharacters ]
            )
        | FieldValueKind.Country ->
            Some(
                message
                    "scalar.country.guidance"
                    [
                        "unknownCode",
                        GuidanceArgument.Code(
                            CountryCode.descriptor.Format.UnknownCode
                            |> Option.defaultWith (fun () ->
                                invalidOp "Country guidance requires the Domain unknown code.")
                        )
                    ]
            )
        // The reference descriptor publishes its permanent grammar; the owning scalar module supplies its bound.
        | FieldValueKind.Reference ->
            Some(
                message
                    "scalar.reference.guidance"
                    [ "maximumCharacters", GuidanceArgument.Count CaseReference.MaximumCharacters ]
            )
        | FieldValueKind.PartyName ->
            Some(
                message
                    "scalar.text.guidance"
                    [ "maximumCharacters", GuidanceArgument.Count(maximum PartyName.descriptor) ]
            )
        | FieldValueKind.Status -> None

    let forReason =
        message
            "scalar.text.guidance"
            [ "maximumCharacters", GuidanceArgument.Count(maximum Reason.descriptor) ]

    let all =
        (Record.fields
         |> List.choose (fun field ->
             forField field.Field |> Option.map (fun guidance -> field.Key, guidance)))
        @ [ "reason", forReason ]
