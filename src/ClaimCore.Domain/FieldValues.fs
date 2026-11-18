namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type ClosureOutcome =
    | Settled
    | Declined
    | Withdrawn
    | Duplicate
    | Other

module ClosureOutcome =
    let vocabulary =
        Vocabulary.ofUnion<ClosureOutcome> "closureOutcome" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type FieldValueKind =
    | CalendarDate
    | Country
    | PartyName
    | Identifier
    | IncidentDescription
    | Amount
    | Currency
    | Reference
    | Status
    | ActorId
    | PlannedDate
    | HandlingSummary
    | NoteText
    | ClosureOutcome
    | Reason

module FieldValueKind =
    let vocabulary =
        Vocabulary.ofUnion<FieldValueKind> "fieldValueKind" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type Admitted =
    | Date of CalendarDate
    | Country of CountryCode
    | Name of PartyName
    | Identifier of Identifier
    | Description of IncidentDescription
    | Amount of Amount
    | Currency of Currency
    | Note of NoteText
    | Outcome of ClosureOutcome
    | Reason of Reason

[<Struct>]
type Authored = private Authored of string

module Authored =
    let create value = Authored value
    let format (Authored value) = value

module FieldValue =
    let kind =
        function
        | Admitted.Date _ -> FieldValueKind.CalendarDate
        | Admitted.Country _ -> FieldValueKind.Country
        | Admitted.Description _ -> FieldValueKind.IncidentDescription
        | Admitted.Name _ -> FieldValueKind.PartyName
        | Admitted.Identifier _ -> FieldValueKind.Identifier
        | Admitted.Amount _ -> FieldValueKind.Amount
        | Admitted.Currency _ -> FieldValueKind.Currency
        | Admitted.Note _ -> FieldValueKind.NoteText
        | Admitted.Outcome _ -> FieldValueKind.ClosureOutcome
        | Admitted.Reason _ -> FieldValueKind.Reason

    let parse kind value =
        match kind with
        | FieldValueKind.CalendarDate -> CalendarDate.parse value |> Result.map Admitted.Date
        | FieldValueKind.Country -> CountryCode.parse value |> Result.map Admitted.Country
        | FieldValueKind.PartyName -> PartyName.parse value |> Result.map Admitted.Name
        | FieldValueKind.Identifier -> Identifier.parse value |> Result.map Admitted.Identifier
        | FieldValueKind.IncidentDescription ->
            IncidentDescription.parse value |> Result.map Admitted.Description
        | FieldValueKind.Amount -> Amount.parse value |> Result.map Admitted.Amount
        | FieldValueKind.Currency -> Currency.parse value |> Result.map Admitted.Currency
        | FieldValueKind.NoteText -> NoteText.parse value |> Result.map Admitted.Note
        | FieldValueKind.Reason -> Reason.parse value |> Result.map Admitted.Reason
        | FieldValueKind.ClosureOutcome ->
            Vocabulary.tryParse ClosureOutcome.vocabulary value
            |> Option.map (Admitted.Outcome >> Ok)
            |> Option.defaultValue (Error Violation.ClosureOutcomeFormat)
        | FieldValueKind.ActorId
        | FieldValueKind.HandlingSummary
        | FieldValueKind.PlannedDate
        | FieldValueKind.Reference
        | FieldValueKind.Status -> invalidArg (nameof kind) "A group contains only business values."

    let format =
        function
        | Admitted.Date value -> CalendarDate.format value
        | Admitted.Country value -> CountryCode.format value
        | Admitted.Description value -> IncidentDescription.format value
        | Admitted.Name value -> PartyName.format value
        | Admitted.Identifier value -> Identifier.format value
        | Admitted.Amount value -> Amount.format value
        | Admitted.Currency value -> Currency.format value
        | Admitted.Note value -> NoteText.format value
        | Admitted.Outcome value -> ClosureOutcome.token value
        | Admitted.Reason value -> Reason.format value

    let admit today =
        function
        | Admitted.Date value -> CalendarDate.admit today value |> Result.map Admitted.Date
        | Admitted.Description value ->
            IncidentDescription.admit value |> Result.map Admitted.Description
        | Admitted.Name value -> PartyName.admit value |> Result.map Admitted.Name
        | Admitted.Identifier value -> Identifier.admit value |> Result.map Admitted.Identifier
        | Admitted.Country value -> CountryCode.admit value |> Result.map Admitted.Country
        | Admitted.Amount value -> Amount.admit value |> Result.map Admitted.Amount
        | Admitted.Currency value -> Currency.admit value |> Result.map Admitted.Currency
        | Admitted.Note value -> NoteText.admit value |> Result.map Admitted.Note
        | Admitted.Reason value -> Reason.admit value |> Result.map Admitted.Reason
        | Admitted.Outcome value -> Ok(Admitted.Outcome value)

    let note =
        function
        | Admitted.Note value -> value
        | _ -> invalidOp "The declared field requires a note."

    let date =
        function
        | Admitted.Date value -> value
        | _ -> invalidOp "The complete group requires a date value."

    let country =
        function
        | Admitted.Country value -> value
        | _ -> invalidOp "The complete group requires a country value."

    let description =
        function
        | Admitted.Description value -> value
        | _ -> invalidOp "The description field requires a description value."

    let name =
        function
        | Admitted.Name value -> value
        | _ -> invalidOp "The complete group requires a name value."

    let identifier =
        function
        | Admitted.Identifier value -> value
        | _ -> invalidOp "The declared field requires an identifier value."

    let amount =
        function
        | Admitted.Amount value -> value
        | _ -> invalidOp "The complete group requires an amount value."

    let currency =
        function
        | Admitted.Currency value -> value
        | _ -> invalidOp "The complete group requires a currency value."
