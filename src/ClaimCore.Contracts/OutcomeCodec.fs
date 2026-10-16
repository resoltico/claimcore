namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open System.Text
open ClaimCore.Application
open WireJson

module OutcomeCodec =
    let private query succeeded =
        function
        | QueryOutcome.Succeeded value -> tagged "SUCCEEDED" (succeeded value)
        | QueryOutcome.Rejected value -> tagged "REJECTED" (WireValues.rejection value)
        | QueryOutcome.Failed value -> tagged "FAILED" (WireValues.fault value)
        | QueryOutcome.Cancelled -> tagged "CANCELLED" null

    let context = query WireValues.actorContext
    let currentCase = query WireValues.record

    let caseList =
        query (fun (value: CaseSummaryPage) ->
            obj
                [
                    "items", value.Items |> List.map WireValues.summary |> array
                    "nextCursor", optional text value.NextCursor
                ])

    let history =
        query (fun (value: HistoryResultPage) ->
            obj
                [
                    "entries", value.Entries |> List.map WireValues.history |> array
                    "nextCursor", optional text value.NextCursor
                ])

    let operation = query WireValues.receipt

    let actors =
        query (fun (value: ActorReviewPage) ->
            obj
                [
                    "items", value.Items |> List.map WireValues.actor |> array
                    "nextCursor", optional text value.NextCursor
                ])

    let preview =
        function
        | PreviewOutcome.Reviewed(value, digest) ->
            tagged
                "REVIEWED"
                (obj [ "review", WireValues.review value; "requestSha256", text digest ])
        | PreviewOutcome.AlreadyAccepted value ->
            tagged "ALREADY_ACCEPTED" (WireValues.receipt value)
        | PreviewOutcome.Rejected value -> tagged "REJECTED" (WireValues.rejection value)
        | PreviewOutcome.Failed value -> tagged "FAILED" (WireValues.fault value)
        | PreviewOutcome.Cancelled -> tagged "CANCELLED" null

    let execute =
        function
        | ExecuteOutcome.Accepted value -> tagged "ACCEPTED" (WireValues.receipt value)
        | ExecuteOutcome.AlreadyAccepted value ->
            tagged "ALREADY_ACCEPTED" (WireValues.receipt value)
        | ExecuteOutcome.Rejected value -> tagged "REJECTED" (WireValues.rejection value)
        | ExecuteOutcome.Failed value -> tagged "FAILED" (WireValues.fault value)
        | ExecuteOutcome.Cancelled -> tagged "CANCELLED" null
        | ExecuteOutcome.OutcomeUnknown(id, digest) ->
            tagged "OUTCOME_UNKNOWN" (obj [ "operationId", uuid id; "requestSha256", text digest ])

    let session authenticated antiforgery =
        tagged
            "SUCCEEDED"
            (obj
                [
                    "authenticated", boolean authenticated
                    "wireFingerprint",
                    text (WireContractFingerprint.value EndpointCatalog.fingerprint)
                    "antiforgeryToken", optional text antiforgery
                ])

    let logout () = tagged "SUCCEEDED" null

    let private encodeEnvelope endpoint members =
        let bytes = obj members |> fun value -> Encoding.UTF8.GetBytes(value.ToJsonString())

        match EndpointCatalog.admitResponse endpoint bytes with
        | Ok _ -> bytes
        | Error _ -> invalidArg "outcome" "The outcome does not satisfy the endpoint contract."

    let encode endpoint outcome =
        encodeEnvelope endpoint [ "endpoint", text endpoint; "outcome", outcome ]

    let encodeOperation endpoint operationId outcome =
        encodeEnvelope
            endpoint
            [
                "endpoint", text endpoint
                "operationId", uuid operationId
                "outcome", outcome
            ]
