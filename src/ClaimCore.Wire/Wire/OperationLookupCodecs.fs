namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Json

module OperationLookupCodecs =
    open Codec

    let private data codec = record id |> field "data" id codec

    let reply =
        let accepted =
            record (fun receipt byRequester -> receipt, byRequester)
            |> field "receipt" fst ReceiptCodecs.receipt
            |> field "byRequester" snd boolean
            |> build

        let pending = record id |> field "finalAt" id instant |> build

        let outcome =
            tagged
                "OperationLookupResult"
                [
                    variant
                        "ACCEPTED"
                        (OperationLookupResult.Accepted >> Outcome.Succeeded)
                        (function
                        | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, byRequester)) ->
                            Some(receipt, byRequester)
                        | _ -> None)
                        (data accepted)
                    variant
                        "PENDING"
                        (OperationLookupResult.Pending >> Outcome.Succeeded)
                        (function
                        | Outcome.Succeeded(OperationLookupResult.Pending finalAt) -> Some finalAt
                        | _ -> None)
                        (data pending)
                    variant
                        "NOT_ACCEPTED"
                        (fun () -> Outcome.Succeeded OperationLookupResult.NotAccepted)
                        (function
                        | Outcome.Succeeded OperationLookupResult.NotAccepted -> Some()
                        | _ -> None)
                        (data nullValue)
                    variant
                        "REJECTED"
                        Outcome.Rejected
                        (function
                        | Outcome.Rejected refusal -> Some refusal
                        | _ -> None)
                        (data DiagnosticCodecs.diagnostic)
                    variant
                        "FAILED"
                        Outcome.Failed
                        (function
                        | Outcome.Failed fault -> Some fault
                        | _ -> None)
                        (data DiagnosticCodecs.fault)
                ]

        ReplyCodecs.operationReply "OperationGetReply" outcome
