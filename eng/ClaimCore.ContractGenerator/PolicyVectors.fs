namespace ClaimCore.ContractGeneration

open ClaimCore.Contract

open System
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ClaimCore.Contract.Protocol

/// Frozen scenarios supply inputs; published expected results come from the typed policy.
module PolicyVectors =
    let private text name (value: JsonElement) =
        value.GetProperty(name: string).GetString() |> nonNull

    let private optional name (value: JsonElement) =
        match value.TryGetProperty(name: string) with
        | true, property when property.ValueKind = JsonValueKind.String ->
            Some(property.GetString() |> nonNull)
        | _ -> None

    let private decode codec value =
        match Codec.decode 1024 codec (JsonSerializer.SerializeToUtf8Bytes(value: string)) with
        | Ok decoded -> decoded
        | Error _ -> invalidOp "An embedded policy scenario has an unknown token."

    let private encode codec value =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(Codec.encode codec value))
        document.RootElement.GetString() |> nonNull

    let private endpoint value =
        Endpoint.tryParse (text "endpoint" value)
        |> Option.defaultWith (fun () -> invalidOp "An embedded policy endpoint is unknown.")

    let private phase = Codec.enumeration DeliveryTokens.phases
    let private tags = Codec.enumeration DeliveryTokens.outcomes

    let private attempt value =
        match text "kind" value with
        | "NOT_DISPATCHED" -> AttemptResult.NotDispatched
        | "DISPATCHED_WITHOUT_REPLY" -> AttemptResult.DispatchedWithoutReply
        | "HOST_FAILURE" ->
            AttemptResult.HostFailure(optional "phase" value |> Option.map (decode phase))
        | "OUTCOME" ->
            AttemptResult.Outcome(decode tags (text "tag" value), optional "diagnostic" value)
        | _ -> invalidOp "An embedded delivery scenario has an unknown attempt."

    let private accumulated =
        function
        | "CONTENT_EXCLUDED" -> AccumulatedKnowledge.ContentExcluded
        | value -> AccumulatedKnowledge.Known(decode Frames.knowledge value)

    let private accumulatedToken =
        function
        | AccumulatedKnowledge.ContentExcluded -> "CONTENT_EXCLUDED"
        | AccumulatedKnowledge.Known value -> encode Frames.knowledge value

    let private transform
        (group: string)
        (memberName: string)
        (compute: JsonElement -> JsonValue | null)
        (root: JsonNode)
        =
        for row in (root[group] |> nonNull).AsArray() do
            let row = nonNull row
            use input = JsonDocument.Parse(row.ToJsonString())
            row[memberName] <- compute input.RootElement

    let private render (root: JsonNode) =
        root.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"
        |> Encoding.UTF8.GetBytes

    let delivery bytes =
        let root = JsonNode.Parse(bytes: byte array) |> nonNull

        root["purpose"] <-
            JsonValue.Create
                "ClaimCore.Contract.Protocol delivery results over frozen independent oracle scenarios"

        transform
            "knowledge"
            "knowledge"
            (fun row ->
                Delivery.knowledge (endpoint row) (attempt (row.GetProperty "attempt"))
                |> encode Frames.knowledge
                |> JsonValue.Create)
            root

        transform
            "evidence"
            "evidence"
            (fun row ->
                Delivery.evidence (endpoint row) (attempt (row.GetProperty "attempt"))
                |> encode Frames.evidence
                |> JsonValue.Create)
            root

        transform
            "accumulate"
            "result"
            (fun row ->
                Delivery.accumulate
                    (accumulated (text "prior" row))
                    (decode Frames.knowledge (text "attempt" row))
                    (decode Frames.evidence (text "evidence" row))
                |> accumulatedToken
                |> JsonValue.Create)
            root

        render root

    let private guid name value = Guid.Parse(text name value)

    let private number name (value: JsonElement) =
        value.GetProperty(name: string).GetInt64()

    let private request endpoint value =
        match endpoint with
        | Endpoint.CasePreview
        | Endpoint.CaseExecute ->
            RequestIdentity.Case(
                guid "operationId" value,
                text "caseReference" value,
                number "expectedRevision" value,
                text "kind" (value.GetProperty "act")
            )
        | Endpoint.AuthorityExecute ->
            RequestIdentity.Authority(
                guid "operationId" value,
                text "kind" (value.GetProperty "act")
            )
        | Endpoint.AuthorityGrants -> RequestIdentity.Actor(guid "actorId" value)
        | Endpoint.OperationObserve -> RequestIdentity.Operation(guid "operationId" value)
        | _ -> RequestIdentity.Read

    let private receipt envelope (data: JsonElement) =
        ReplyIdentity.Receipt(
            envelope,
            {
                OperationId = guid "operationId" data
                Scope =
                    match text "scope" data with
                    | "CASE" -> ReceiptScopeKind.Case
                    | "AUTHORITY" -> ReceiptScopeKind.Authority
                    | _ -> invalidOp "An embedded receipt has an unknown scope."
                Act = text "act" data
                CaseReference = optional "caseReference" data
                Revision =
                    match data.TryGetProperty "revision" with
                    | true, value -> Some(value.GetInt64())
                    | _ -> None
            }
        )

    let private reply endpoint (value: JsonElement) =
        let envelope = optional "operationId" value |> Option.map Guid.Parse
        let outcome = value.GetProperty "outcome"
        let data = outcome.GetProperty "data"

        match text "tag" outcome with
        | "ACCEPTED"
        | "ALREADY_ACCEPTED" -> receipt envelope data
        | "REVIEWED" ->
            let after = data.GetProperty("review").GetProperty "after"

            ReplyIdentity.Reviewed(
                Option.get envelope,
                text "caseReference" after,
                number "revision" after
            )
        | "OUTCOME_UNKNOWN" -> ReplyIdentity.Unknown(Option.get envelope, guid "operationId" data)
        | "SUCCEEDED" when endpoint = Endpoint.OperationObserve ->
            ReplyIdentity.Observed(guid "operationId" data)
        | "SUCCEEDED" when endpoint = Endpoint.AuthorityGrants ->
            ReplyIdentity.Actor(guid "actorId" (data.GetProperty "actor"))
        | _ -> ReplyIdentity.Other envelope

    let correlation bytes =
        let root = JsonNode.Parse(bytes: byte array) |> nonNull

        root["purpose"] <-
            JsonValue.Create
                "ClaimCore.Contract.Protocol correlation results over frozen independent oracle scenarios"

        transform
            "cases"
            "correlated"
            (fun row ->
                let endpoint = endpoint row

                Correlation.matchesIdentity
                    (request endpoint (row.GetProperty "request"))
                    (reply endpoint (row.GetProperty "reply"))
                |> JsonValue.Create)
            root

        render root
