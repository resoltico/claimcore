namespace ClaimCore.Wire

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Json


module ReplyCodecs =
    open Codec

    let private data codec = record id |> field "data" id codec

    let readReply name (codec: Codec<'T>) : Codec<Outcome<Presented<'T>>> =
        let outcome =
            tagged
                (name + ".Outcome")
                [
                    variant
                        "SUCCEEDED"
                        Outcome.Succeeded
                        (function
                        | Outcome.Succeeded value -> Some value
                        | _ -> None)
                        ((record (fun data view -> { Presented.Data = data; View = view })
                         : Fields<Presented<'T>, _>)
                         |> field "data" _.Data codec
                         |> field "view" _.View ScreenCodecs.screen)
                    variant
                        "REJECTED"
                        Outcome.Rejected
                        (function
                        | Outcome.Rejected value -> Some value
                        | _ -> None)
                        (record id |> field "rejection" id DiagnosticCodecs.diagnostic)
                    |> namedTaggedVariant "DataRejected"
                    variant
                        "FAILED"
                        Outcome.Failed
                        (function
                        | Outcome.Failed value -> Some value
                        | _ -> None)
                        (record id |> field "fault" id DiagnosticCodecs.fault)
                    |> namedTaggedVariant "Failed"
                ]

        record id |> field "outcome" id outcome |> build |> named name

    let dataReadReply name (codec: Codec<'value>) : Codec<Outcome<'value>> =
        let outcome =
            tagged
                (name + ".Outcome")
                [
                    variant
                        "SUCCEEDED"
                        Outcome.Succeeded
                        (function
                        | Outcome.Succeeded value -> Some value
                        | _ -> None)
                        (data codec)
                    variant
                        "REJECTED"
                        Outcome.Rejected
                        (function
                        | Outcome.Rejected value -> Some value
                        | _ -> None)
                        (record id |> field "rejection" id DiagnosticCodecs.diagnostic)
                    |> namedTaggedVariant "DataRejected"
                    variant
                        "FAILED"
                        Outcome.Failed
                        (function
                        | Outcome.Failed value -> Some value
                        | _ -> None)
                        (record id |> field "fault" id DiagnosticCodecs.fault)
                    |> namedTaggedVariant "Failed"
                ]

        record id |> field "outcome" id outcome |> build |> named name

    let operationReply name outcome =
        (record (fun operationId value ->
            {
                OperationReply.OperationId = operationId
                Outcome = value
            })
        : Fields<OperationReply<'value>, _>)
        |> field "operationId" _.OperationId uuid
        |> flatten _.Outcome (record id |> field "outcome" id outcome)
        |> build
        |> named name

    let private target =
        tagged
            "ReviewTarget"
            [
                variant
                    "CASE"
                    (fun (id, reference) -> ReviewTarget.Case(id, reference))
                    (function
                    | ReviewTarget.Case(id, reference) -> Some(id, reference)
                    | _ -> None)
                    (record (fun id reference -> id, reference)
                     |> field "caseId" fst ResponseScalars.caseId
                     |> field "caseReference" snd ResponseScalars.caseReference)
                variant
                    "OPENING"
                    ReviewTarget.Opening
                    (function
                    | ReviewTarget.Opening value -> Some value
                    | _ -> None)
                    (record id
                     |> optionalField "requestedReference" id ResponseScalars.caseReference)
                variant
                    "PERSON"
                    ReviewTarget.Person
                    (function
                    | ReviewTarget.Person value -> Some value
                    | _ -> None)
                    (record id |> field "actor" id (nullable ActorCodecs.actorRef))
            ]

    let review =
        (record
            (fun task target references binding changes opening context changesOwnSignInIdentity ->
                {
                    OperationReview.Task = task
                    Target = target
                    References = references
                    Binding = binding
                    Changes = changes
                    Opening = opening
                    Context = context
                    ChangesOwnSignInIdentity = defaultArg changesOwnSignInIdentity false
                })
        : Fields<OperationReview, _>)
        |> field "task" _.Task TaskVocabulary.codec
        |> field "target" _.Target target
        |> field "references" _.References PartDescriptionCodecs.references
        |> field "binding" _.Binding ClaimCore.RecordFormat.BindingCodecs.binding
        |> field "changes" _.Changes (list 50 PartDescriptionCodecs.part)
        |> optionalField "opening" _.Opening PartDescriptionCodecs.opening
        |> field "context" _.Context ResponseCodecs.runtimeContext
        |> optionalField
            "changesOwnSignInIdentity"
            (fun review -> if review.ChangesOwnSignInIdentity then Some true else None)
            boolean
        |> build
        |> named "OperationReview"

    let private reviewed = record id |> field "review" id review |> build

    let private previewResult =
        tagged
            "PreviewResult"
            [
                variant
                    "REVIEWED"
                    (Previewed.Reviewed >> Outcome.Succeeded)
                    (function
                    | Outcome.Succeeded(Previewed.Reviewed value) -> Some value
                    | _ -> None)
                    (data reviewed)
                variant
                    "ALREADY_ACCEPTED"
                    (Previewed.AlreadyAccepted >> Outcome.Succeeded)
                    (function
                    | Outcome.Succeeded(Previewed.AlreadyAccepted value) -> Some value
                    | _ -> None)
                    (data ReceiptCodecs.receipt)
                variant
                    "REJECTED"
                    Outcome.Rejected
                    (function
                    | Outcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "rejection" id DiagnosticCodecs.diagnostic)
                |> namedTaggedVariant "DataRejected"
                variant
                    "FAILED"
                    Outcome.Failed
                    (function
                    | Outcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "fault" id DiagnosticCodecs.fault)
                |> namedTaggedVariant "Failed"
            ]

    let preview =
        (record (fun id outcome ->
            {
                PreviewReply.OperationId = id
                Outcome = outcome
            })
        : Fields<PreviewReply, _>)
        |> field "operationId" _.OperationId Codec.uuid
        |> field "outcome" _.Outcome previewResult
        |> build
        |> named "DataPreviewReply"

    let private executeResult =
        tagged
            "ExecuteResult"
            [
                variant
                    "ACCEPTED"
                    ExecuteResult.Accepted
                    (function
                    | ExecuteResult.Accepted value -> Some value
                    | _ -> None)
                    (data ReceiptCodecs.receipt)
                variant
                    "ALREADY_ACCEPTED"
                    ExecuteResult.AlreadyAccepted
                    (function
                    | ExecuteResult.AlreadyAccepted value -> Some value
                    | _ -> None)
                    (data ReceiptCodecs.receipt)
                variant
                    "REJECTED"
                    ExecuteResult.Rejected
                    (function
                    | ExecuteResult.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "rejection" id DiagnosticCodecs.diagnostic)
                |> namedTaggedVariant "DataRejected"
                variant
                    "FAILED"
                    ExecuteResult.Failed
                    (function
                    | ExecuteResult.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "fault" id DiagnosticCodecs.fault)
                |> namedTaggedVariant "Failed"
                variant
                    "OUTCOME_UNKNOWN"
                    (fun () -> ExecuteResult.OutcomeUnknown)
                    (function
                    | ExecuteResult.OutcomeUnknown -> Some()
                    | _ -> None)
                    (data nullValue)
            ]

    let execute =
        (record (fun id outcome ->
            {
                ExecuteReply.OperationId = id
                Outcome = outcome
            })
        : Fields<ExecuteReply, _>)
        |> field "operationId" _.OperationId Codec.uuid
        |> field "outcome" _.Outcome executeResult
        |> build
        |> named "DataExecuteReply"
