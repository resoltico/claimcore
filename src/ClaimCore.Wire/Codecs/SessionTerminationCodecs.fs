namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Json

module SessionTerminationCodecs =
    open Codec

    let private summary =
        (record (fun count self ->
            {
                SessionTermination.Count = count
                Self = self
            })
        : Fields<SessionTermination, _>)
        |> field "count" _.Count (integer 0L SafeInteger.Maximum)
        |> field "self" _.Self boolean
        |> build

    let data =
        tagged
            "SessionTerminationOutcome"
            [
                variant
                    "SUCCEEDED"
                    SessionTerminationResult.Succeeded
                    (function
                    | SessionTerminationResult.Succeeded value -> Some value
                    | _ -> None)
                    (record id |> field "data" id summary)
                variant
                    "REJECTED"
                    SessionTerminationResult.Rejected
                    (function
                    | SessionTerminationResult.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "rejection" id DiagnosticCodecs.diagnostic)
                variant
                    "FAILED"
                    SessionTerminationResult.Failed
                    (function
                    | SessionTerminationResult.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "fault" id DiagnosticCodecs.fault)
                unitVariant "RETIREMENT_CONFIRMED" SessionTerminationResult.RetirementConfirmed
                unitVariant "OUTCOME_UNKNOWN" SessionTerminationResult.OutcomeUnknown
            ]
        |> fun outcome ->
            record id
            |> field "outcome" id outcome
            |> build
            |> named "SessionTerminationDataResult"

    let human =
        tagged
            "SessionTerminationHumanOutcome"
            [
                variant
                    "SUCCEEDED"
                    HumanSessionTerminationResult.Succeeded
                    (function
                    | HumanSessionTerminationResult.Succeeded(self, document) ->
                        Some(self, document)
                    | _ -> None)
                    (record (fun self document -> self, document)
                     |> field "self" fst boolean
                     |> field "document" snd HumanDocumentCodecs.document)
                variant
                    "REJECTED"
                    HumanSessionTerminationResult.Rejected
                    (function
                    | HumanSessionTerminationResult.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "rejection" id HumanDocumentCodecs.rejection)
                variant
                    "FAILED"
                    HumanSessionTerminationResult.Failed
                    (function
                    | HumanSessionTerminationResult.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "fault" id DiagnosticCodecs.fault)
                variant
                    "RETIREMENT_CONFIRMED"
                    HumanSessionTerminationResult.RetirementConfirmed
                    (function
                    | HumanSessionTerminationResult.RetirementConfirmed value -> Some value
                    | _ -> None)
                    (record id |> field "document" id HumanDocumentCodecs.document)
                variant
                    "OUTCOME_UNKNOWN"
                    HumanSessionTerminationResult.OutcomeUnknown
                    (function
                    | HumanSessionTerminationResult.OutcomeUnknown value -> Some value
                    | _ -> None)
                    (record id |> field "document" id HumanDocumentCodecs.document)
            ]
        |> fun outcome ->
            record id
            |> field "outcome" id outcome
            |> build
            |> named "SessionTerminationHumanResult"

    let classify =
        function
        | RepresentationReply.Data value -> SessionTerminationResult.classify value
        | RepresentationReply.Human(HumanSessionTerminationResult.Succeeded _)
        | RepresentationReply.Human(HumanSessionTerminationResult.RetirementConfirmed _) ->
            ReplyKind.Succeeded
        | RepresentationReply.Human(HumanSessionTerminationResult.Rejected _) -> ReplyKind.Rejected
        | RepresentationReply.Human(HumanSessionTerminationResult.Failed _) -> ReplyKind.Failed
        | RepresentationReply.Human(HumanSessionTerminationResult.OutcomeUnknown _) ->
            ReplyKind.OutcomeUnknown
