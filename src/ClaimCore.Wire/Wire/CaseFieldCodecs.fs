namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Json

/// Typed case values share scalar grammars across full facts, listed projections and differences.
module internal CaseFieldCodecs =
    open Codec
    open ResponseScalars

    let scalar kind =
        let codec =
            match kind with
            | FieldValueKind.CalendarDate -> date
            | FieldValueKind.Country -> country
            | FieldValueKind.PartyName -> boundedUtf8Text 1 PartyName.MaximumBytes
            | FieldValueKind.Identifier -> boundedUtf8Text 1 Identifier.MaximumBytes
            | FieldValueKind.IncidentDescription ->
                boundedUtf8Text 1 IncidentDescription.MaximumBytes
            | FieldValueKind.HandlingSummary -> nonempty
            | FieldValueKind.Amount -> amount
            | FieldValueKind.Currency -> currency
            | FieldValueKind.ActorId
            | FieldValueKind.PlannedDate
            | FieldValueKind.Reference
            | FieldValueKind.Status -> invalidArg (nameof kind) "A group contains business values."

        codec
        |> interpret
            (fun path raw ->
                FieldValue.parse kind raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "value outside its wire grammar"
                    }))
            FieldValue.format

    let forKind kind =
        match kind with
        | FieldValueKind.Reference ->
            caseReference
            |> map CaseFieldValue.Reference (function
                | CaseFieldValue.Reference value -> value
                | _ -> invalidArg "value" "Expected a case reference.")
        | FieldValueKind.Status ->
            VocabularyCodec.create CaseStatus.vocabulary
            |> map CaseFieldValue.Status (function
                | CaseFieldValue.Status value -> value
                | _ -> invalidArg "value" "Expected a case status.")
        | _ ->
            scalar kind
            |> map CaseFieldValue.Business (function
                | CaseFieldValue.Business value when FieldValue.kind value = kind -> value
                | _ -> invalidArg "value" "Expected the declared business scalar.")

    let value =
        let business token kind =
            variant
                token
                CaseFieldValue.Business
                (function
                | CaseFieldValue.Business value when FieldValue.kind value = kind -> Some value
                | _ -> None)
                (record id |> field "value" id (scalar kind))

        tagged
            "CaseFieldValue"
            [
                business "DATE" FieldValueKind.CalendarDate
                business "COUNTRY" FieldValueKind.Country
                business "PARTY" FieldValueKind.PartyName
                business "IDENTIFIER" FieldValueKind.Identifier
                business "TEXT" FieldValueKind.IncidentDescription
                business "AMOUNT" FieldValueKind.Amount
                business "CURRENCY" FieldValueKind.Currency
                variant
                    "REFERENCE"
                    CaseFieldValue.Reference
                    (function
                    | CaseFieldValue.Reference value -> Some value
                    | _ -> None)
                    (record id |> field "value" id caseReference)
                variant
                    "STATUS"
                    CaseFieldValue.Status
                    (function
                    | CaseFieldValue.Status value -> Some value
                    | _ -> None)
                    (record id |> field "value" id (VocabularyCodec.create CaseStatus.vocabulary))
            ]
