namespace ClaimCore.Protocol

open ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json


module ResolutionCodecs =
    open Codec

    let request = RequestCodecs.resolveRequest

    let private data codec = record id |> field "data" id codec

    let reply =
        let outcome =
            tagged
                "ResolveResult"
                [
                    variant
                        "ACCEPTED"
                        (Resolved.Accepted >> Outcome.Succeeded)
                        (function
                        | Outcome.Succeeded(Resolved.Accepted value) -> Some value
                        | _ -> None)
                        (data ReceiptCodecs.receipt)
                    variant
                        "NOT_ACCEPTED"
                        (fun () -> (Outcome.Succeeded Resolved.NotAccepted))
                        (function
                        | Outcome.Succeeded Resolved.NotAccepted -> Some()
                        | _ -> None)
                        (data nullValue)
                    variant
                        "REJECTED"
                        Outcome.Rejected
                        (function
                        | Outcome.Rejected value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.diagnostic)
                    variant
                        "FAILED"
                        Outcome.Failed
                        (function
                        | Outcome.Failed value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.fault)
                ]

        ReplyCodecs.operationReply "ResolveReply" outcome

    let status =
        let outcome =
            tagged
                "StatusResult"
                [
                    variant
                        "ACCEPTED"
                        (OperationStatus.Accepted >> Outcome.Succeeded)
                        (function
                        | Outcome.Succeeded(OperationStatus.Accepted value) -> Some value
                        | _ -> None)
                        (data ReceiptCodecs.receipt)
                    variant
                        "NOT_ACCEPTED"
                        (fun () -> (Outcome.Succeeded OperationStatus.NotAccepted))
                        (function
                        | Outcome.Succeeded OperationStatus.NotAccepted -> Some()
                        | _ -> None)
                        (data nullValue)
                    variant
                        "UNRESOLVED"
                        (fun () -> Outcome.Succeeded OperationStatus.Unresolved)
                        (function
                        | Outcome.Succeeded OperationStatus.Unresolved -> Some()
                        | _ -> None)
                        (data nullValue)
                    variant
                        "REJECTED"
                        Outcome.Rejected
                        (function
                        | Outcome.Rejected value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.diagnostic)
                    variant
                        "FAILED"
                        Outcome.Failed
                        (function
                        | Outcome.Failed value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.fault)
                ]

        ReplyCodecs.operationReply "StatusReply" outcome
