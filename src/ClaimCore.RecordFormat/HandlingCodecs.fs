namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

module HandlingCodecs =
    open Codec

    let private handlingWith name fields =
        tagged
            name
            [
                unitVariant "OPEN" Handling.Open
                variant "CLOSED" Handling.Closed Handling.closure fields
            ]

    let private outcome = VocabularyCodec.create ClosureOutcome.vocabulary

    let retainedHandling =
        (record (fun outcome ->
            {
                Closure.Outcome = outcome
                Note = None
            })
        : Fields<Closure, _>)
        |> field "outcome" _.Outcome outcome
        |> handlingWith "RetainedHandling"

    let value =
        (record (fun outcome note ->
            {
                Closure.Outcome = outcome
                Note = note
            })
        : Fields<Closure, _>)
        |> field "outcome" _.Outcome outcome
        |> optionalField "note" _.Note (RecordScalars.scalar NoteText.parse NoteText.format)
        |> handlingWith "Handling"
