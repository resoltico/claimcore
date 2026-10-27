namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract

open ClaimCore.Domain

open ClaimCore.Json


module ReplyCodecs =
    open Codec

    let private data codec = record id |> field "data" id codec

    let readReply name (codec: Codec<'T>) : Codec<ReadReply<'T>> =
        let outcome =
            tagged
                (name + ".Outcome")
                [
                    variant
                        "SUCCEEDED"
                        ReadReply.Succeeded
                        (function
                        | ReadReply.Succeeded value -> Some value
                        | _ -> None)
                        (data codec)
                    variant
                        "REJECTED"
                        ReadReply.Rejected
                        (function
                        | ReadReply.Rejected value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.diagnostic)
                    variant
                        "FAILED"
                        ReadReply.Failed
                        (function
                        | ReadReply.Failed value -> Some value
                        | _ -> None)
                        (data DiagnosticCodecs.fault)
                ]

        record id |> field "outcome" id outcome |> build |> named name

    let authorityState =
        (record (fun enabled grants -> { Enabled = enabled; Grants = grants })
        : Fields<AuthorityState, _>)
        |> field "enabled" _.Enabled boolean
        |> field "grants" _.Grants (list 1 ResponseCodecs.authorityGrant)
        |> build
        |> named "AuthorityState"

    let private authorityReviewFields =
        (record (fun target before after ->
            {
                Target = target
                Before = before
                After = after
            })
        : Fields<AuthorityReview, _>)
        |> field "target" _.Target (nullable ResponseCodecs.actorSummary)
        |> field "before" _.Before authorityState
        |> field "after" _.After authorityState

    let review =
        let subject =
            tagged
                "ReviewSubject"
                [
                    variant
                        "CASE"
                        OperationReview.Case
                        (function
                        | OperationReview.Case value -> Some value
                        | _ -> None)
                        ResponseCodecs.reviewFields
                    variant
                        "AUTHORITY"
                        OperationReview.Authority
                        (function
                        | OperationReview.Authority value -> Some value
                        | _ -> None)
                        authorityReviewFields
                ]

        record id |> field "subject" id subject |> build |> named "OperationReview"

    let private reviewed = record id |> field "review" id review |> build

    let private previewResult =
        tagged
            "PreviewResult"
            [
                variant
                    "REVIEWED"
                    PreviewResult.Reviewed
                    (function
                    | PreviewResult.Reviewed value -> Some value
                    | _ -> None)
                    (data reviewed)
                variant
                    "ALREADY_ACCEPTED"
                    PreviewResult.AlreadyAccepted
                    (function
                    | PreviewResult.AlreadyAccepted value -> Some value
                    | _ -> None)
                    (data ReceiptCodecs.receipt)
                variant
                    "REJECTED"
                    PreviewResult.Rejected
                    (function
                    | PreviewResult.Rejected value -> Some value
                    | _ -> None)
                    (data DiagnosticCodecs.diagnostic)
                variant
                    "FAILED"
                    PreviewResult.Failed
                    (function
                    | PreviewResult.Failed value -> Some value
                    | _ -> None)
                    (data DiagnosticCodecs.fault)
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
        |> named "PreviewReply"

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
                    (data DiagnosticCodecs.diagnostic)
                variant
                    "FAILED"
                    ExecuteResult.Failed
                    (function
                    | ExecuteResult.Failed value -> Some value
                    | _ -> None)
                    (data DiagnosticCodecs.fault)
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
        |> named "ExecuteReply"
