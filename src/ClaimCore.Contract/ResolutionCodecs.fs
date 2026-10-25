namespace ClaimCore.Contract

open ClaimCore.Contract.Protocol

module ResolutionCodecs =
    open Codec

    let request =
        (record (fun id -> { ResolveRequest.OperationId = id }): Fields<ResolveRequest, _>)
        |> field "operationId" _.OperationId uuid
        |> build
        |> named "ResolveRequest"

    let private data codec = record id |> field "data" id codec

    let reply =
        let outcome =
            tagged
                "ResolveResult"
                "tag"
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
