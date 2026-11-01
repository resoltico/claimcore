namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type FieldValueKind =
    | CalendarDate
    | Country
    | PartyName
    | Amount
    | Currency
    | Reference
    | Status
    | ActorId
    | PlannedDate

[<RequireQualifiedAccess>]
type Admitted =
    | Date of CalendarDate
    | Country of CountryCode
    | Name of PartyName
    | Amount of Amount
    | Currency of Currency

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
        | Admitted.Name _ -> FieldValueKind.PartyName
        | Admitted.Amount _ -> FieldValueKind.Amount
        | Admitted.Currency _ -> FieldValueKind.Currency

    let parse kind value =
        match kind with
        | FieldValueKind.CalendarDate -> CalendarDate.parse value |> Result.map Admitted.Date
        | FieldValueKind.Country -> CountryCode.parse value |> Result.map Admitted.Country
        | FieldValueKind.PartyName -> PartyName.parse value |> Result.map Admitted.Name
        | FieldValueKind.Amount -> Amount.parse value |> Result.map Admitted.Amount
        | FieldValueKind.Currency -> Currency.parse value |> Result.map Admitted.Currency
        | FieldValueKind.ActorId
        | FieldValueKind.PlannedDate
        | FieldValueKind.Reference
        | FieldValueKind.Status -> invalidArg (nameof kind) "A group contains only business values."

    let format =
        function
        | Admitted.Date value -> CalendarDate.format value
        | Admitted.Country value -> CountryCode.format value
        | Admitted.Name value -> PartyName.format value
        | Admitted.Amount value -> Amount.format value
        | Admitted.Currency value -> Currency.format value

    let admit today =
        function
        | Admitted.Date value -> CalendarDate.admit today value |> Result.map Admitted.Date
        | Admitted.Name value -> PartyName.admit value |> Result.map Admitted.Name
        | Admitted.Country value -> CountryCode.admit value |> Result.map Admitted.Country
        | Admitted.Amount value -> Amount.admit value |> Result.map Admitted.Amount
        | Admitted.Currency value -> Currency.admit value |> Result.map Admitted.Currency

    let date =
        function
        | Admitted.Date value -> value
        | _ -> invalidOp "The complete group requires a date value."

    let country =
        function
        | Admitted.Country value -> value
        | _ -> invalidOp "The complete group requires a country value."

    let name =
        function
        | Admitted.Name value -> value
        | _ -> invalidOp "The complete group requires a name value."

    let amount =
        function
        | Admitted.Amount value -> value
        | _ -> invalidOp "The complete group requires an amount value."

    let currency =
        function
        | Admitted.Currency value -> value
        | _ -> invalidOp "The complete group requires a currency value."
