namespace ClaimCore.Contract

open ClaimCore.Domain
open ClaimCore.Json

open ClaimCore.Contract.Protocol

module ResolutionCodecs =
    open Codec

    let request = Record.resolve.Codec RequestCodecs.readProfile

    let private data codec = record id |> field "data" id codec

    let reply =
        let outcome =
            tagged
                "ResolveResult"
                [
                    variant
                        "ACCEPTED"
                        ResolveReply.Accepted
                        (function
                        | ResolveReply.Accepted value -> Some value
                        | _ -> None)
                        (data ReceiptCodecs.receipt)
                    variant
                        "NOT_ACCEPTED"
                        (fun () -> ResolveReply.NotAccepted)
                        (function
                        | ResolveReply.NotAccepted -> Some()
                        | _ -> None)
                        (data nullValue)
                    variant
                        "REJECTED"
                        ResolveReply.Rejected
                        (function
                        | ResolveReply.Rejected value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.diagnostic)
                    variant
                        "FAILED"
                        ResolveReply.Failed
                        (function
                        | ResolveReply.Failed value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.fault)
                ]

        record id |> field "outcome" id outcome |> build |> named "ResolveReply"
