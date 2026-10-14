namespace ClaimCore.Contracts

open System.Text
open System.Text.Json.Nodes
open ClaimCore.Application
open WireJson

module OutcomeCodec =
    let private query succeeded =
        function
        | QueryOutcome.Succeeded value -> tagged "SUCCEEDED" (succeeded value)
        | QueryOutcome.Rejected value -> tagged "REJECTED" (WireValues.rejection value)
        | QueryOutcome.Failed value -> tagged "FAILED" (WireValues.fault value)
        | QueryOutcome.Cancelled -> tagged "CANCELLED" null

    let private lookup foundName found identityName identity =
        function
        | Lookup.Found value -> obj [ "tag", text "FOUND"; foundName, found value ]
        | Lookup.NotFound value -> obj [ "tag", text "NOT_FOUND"; identityName, identity value ]

    let definition =
        query (fun value ->
            obj
                [
                    "semanticFingerprint",
                    text (SemanticCoreFingerprint.value value.SemanticFingerprint)
                    "wireFingerprint",
                    text (WireContractFingerprint.value EndpointCatalog.fingerprint)
                    "runtime", WireValues.context value.Runtime
                    "definition",
                    (JsonNode.Parse(
                        CanonicalContract.bytes (ContractJson.semanticContract value.Contract)
                     )
                     |> nonNull)
                ])

    let currentCase = query (lookup "current" WireValues.record "caseReference" text)

    let caseList =
        query (fun (value: CaseSummaryPage) ->
            obj
                [
                    "items", (value.Items |> List.map WireValues.summary |> array)
                    "nextCursor", optional text value.NextCursor
                    "availableCommands", WireValues.commands value.AvailableCommands
                ])

    let history =
        query (function
            | Lookup.Found(value: HistoryResultPage) ->
                obj
                    [
                        "tag", text "FOUND"
                        "entries", (value.Entries |> List.map WireValues.history |> array)
                        "nextCursor", optional text value.NextCursor
                    ]
            | Lookup.NotFound reference ->
                obj [ "tag", text "NOT_FOUND"; "caseReference", text reference ])

    let operation = query (lookup "receipt" WireValues.receipt "operationId" uuid)

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

    let authority =
        function
        | AuthorityOutcome.Applied(id, position, target) ->
            tagged
                "APPLIED"
                (obj
                    [
                        "eventId", uuid id
                        "position", WireValues.position position
                        "targetActorId", uuid target
                    ])
        | AuthorityOutcome.AlreadyApplied(id, position, target) ->
            tagged
                "ALREADY_APPLIED"
                (obj
                    [
                        "eventId", uuid id
                        "position", WireValues.position position
                        "targetActorId", uuid target
                    ])
        | AuthorityOutcome.Rejected value -> tagged "REJECTED" (WireValues.rejection value)
        | AuthorityOutcome.Failed value -> tagged "FAILED" (WireValues.fault value)
        | AuthorityOutcome.Cancelled -> tagged "CANCELLED" null
        | AuthorityOutcome.OutcomeUnknown id ->
            tagged "OUTCOME_UNKNOWN" (obj [ "eventId", uuid id ])

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

    let encode endpoint outcome =
        let bytes =
            obj [ "endpoint", text endpoint; "outcome", outcome ]
            |> fun value -> value.ToJsonString() |> Encoding.UTF8.GetBytes

        match EndpointCatalog.admitResponse endpoint bytes with
        | Ok _ -> bytes
        | Error _ -> invalidArg "outcome" "The outcome does not satisfy the endpoint contract."
