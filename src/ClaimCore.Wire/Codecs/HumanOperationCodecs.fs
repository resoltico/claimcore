namespace ClaimCore.Wire

open ClaimCore.Presentation
open ClaimCore.Json

/// HUMAN operation transport contains authored documents and diagnostics, without DATA snapshots.
module HumanOperationCodecs =
    open Codec

    let private reply name outcome =
        (record (fun id outcome ->
            {
                HumanOperationReply.OperationId = id
                Outcome = outcome
            })
        : Fields<HumanOperationReply<_>, _>)
        |> field "operationId" _.OperationId uuid
        |> field "outcome" _.Outcome outcome
        |> build
        |> named name

    let private document = record id |> field "document" id HumanDocumentCodecs.document

    let private receipt =
        record id |> field "document" id HumanDocumentCodecs.receiptDocument

    let private rejection =
        record id |> field "rejection" id HumanDocumentCodecs.rejection

    let private failure = record id |> field "fault" id DiagnosticCodecs.fault

    let preview =
        tagged
            "HumanPreviewResult"
            [
                variant
                    "REVIEWED"
                    HumanPreviewResult.Reviewed
                    (function
                    | HumanPreviewResult.Reviewed value -> Some value
                    | _ -> None)
                    (record id |> field "review" id HumanDocumentCodecs.reviewDocument)
                variant
                    "ALREADY_ACCEPTED"
                    HumanPreviewResult.AlreadyAccepted
                    (function
                    | HumanPreviewResult.AlreadyAccepted value -> Some value
                    | _ -> None)
                    receipt
                variant
                    "REJECTED"
                    HumanPreviewResult.Rejected
                    (function
                    | HumanPreviewResult.Rejected value -> Some value
                    | _ -> None)
                    rejection
                |> namedTaggedVariant "HumanRejected"
                variant
                    "FAILED"
                    HumanPreviewResult.Failed
                    (function
                    | HumanPreviewResult.Failed value -> Some value
                    | _ -> None)
                    failure
                |> namedTaggedVariant "Failed"
            ]
        |> reply "HumanPreviewReply"

    let execute =
        tagged
            "HumanExecuteResult"
            [
                variant
                    "ACCEPTED"
                    HumanExecuteResult.Accepted
                    (function
                    | HumanExecuteResult.Accepted value -> Some value
                    | _ -> None)
                    receipt
                variant
                    "ALREADY_ACCEPTED"
                    HumanExecuteResult.AlreadyAccepted
                    (function
                    | HumanExecuteResult.AlreadyAccepted value -> Some value
                    | _ -> None)
                    receipt
                variant
                    "REJECTED"
                    HumanExecuteResult.Rejected
                    (function
                    | HumanExecuteResult.Rejected value -> Some value
                    | _ -> None)
                    rejection
                |> namedTaggedVariant "HumanRejected"
                variant
                    "FAILED"
                    HumanExecuteResult.Failed
                    (function
                    | HumanExecuteResult.Failed value -> Some value
                    | _ -> None)
                    failure
                |> namedTaggedVariant "Failed"
                unitVariant "OUTCOME_UNKNOWN" HumanExecuteResult.OutcomeUnknown
            ]
        |> reply "HumanExecuteReply"

    let lookup =
        tagged
            "HumanLookupResult"
            [
                variant
                    "ACCEPTED"
                    HumanLookupResult.Accepted
                    (function
                    | HumanLookupResult.Accepted(document, own) -> Some(document, own)
                    | _ -> None)
                    (record (fun document own -> document, own)
                     |> field "document" fst HumanDocumentCodecs.receiptDocument
                     |> field "byRequester" snd boolean)
                variant
                    "PENDING"
                    HumanLookupResult.Pending
                    (function
                    | HumanLookupResult.Pending(finalAt, document) -> Some(finalAt, document)
                    | _ -> None)
                    (record (fun finalAt document -> finalAt, document)
                     |> field "finalAt" fst instant
                     |> field "document" snd HumanDocumentCodecs.document)
                variant
                    "NOT_ACCEPTED"
                    HumanLookupResult.NotAccepted
                    (function
                    | HumanLookupResult.NotAccepted value -> Some value
                    | _ -> None)
                    document
                variant
                    "REJECTED"
                    HumanLookupResult.Rejected
                    (function
                    | HumanLookupResult.Rejected value -> Some value
                    | _ -> None)
                    rejection
                |> namedTaggedVariant "HumanRejected"
                variant
                    "FAILED"
                    HumanLookupResult.Failed
                    (function
                    | HumanLookupResult.Failed value -> Some value
                    | _ -> None)
                    failure
                |> namedTaggedVariant "Failed"
            ]
        |> reply "HumanOperationGetReply"
