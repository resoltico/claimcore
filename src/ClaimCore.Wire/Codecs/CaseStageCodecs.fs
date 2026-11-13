namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Json

module internal CaseStageCodecs =
    open Codec
    open ResponseScalars

    let private kind = VocabularyCodec.create CaseStageKind.vocabulary

    let outline =
        (record (fun current completed ->
            {
                CaseStageOutline.Current = current
                Completed = completed
            })
        : Fields<CaseStageOutline, _>)
        |> field "current" _.Current kind
        |> field "completed" _.Completed (boundedList 1 CaseStageKind.all.Length kind)
        |> build
        |> named "CaseStageOutline"

    let private step =
        (record (fun stage completed date amount ->
            {
                CaseStageEntry.Stage = stage
                Completed = completed
                Date = date
                Amount = amount
            })
        : Fields<CaseStageEntry, _>)
        |> field "stage" _.Stage kind
        |> field "completed" _.Completed boolean
        |> field "date" _.Date (nullable calendarDate)
        |> field "amount" _.Amount (nullable money)
        |> build
        |> named "CaseStageEntry"

    let display =
        tagged
            "CaseStageDisplay"
            [
                variant
                    "TRACKER"
                    (fun (current, steps) -> CaseStageDisplay.Tracker(current, steps))
                    (function
                    | CaseStageDisplay.Tracker(current, steps) -> Some(current, steps)
                    | _ -> None)
                    (record (fun current steps -> current, steps)
                     |> field "current" fst kind
                     |> field
                         "steps"
                         snd
                         (boundedList CaseStageKind.all.Length CaseStageKind.all.Length step))
                unitVariant "ERASED" CaseStageDisplay.Erased
            ]
