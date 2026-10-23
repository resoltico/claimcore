namespace ClaimCore.ContractGeneration

open ClaimCore.Contract

open System
open System.Text.Json
open ClaimCore.Contract.Protocol

/// Exhaustive finite policy projection. Independent scenario oracles remain separate.
module DeliveryTables =
    let private token codec value =
        use document = JsonDocument.Parse(ReadOnlyMemory<byte>(Codec.encode codec value))
        document.RootElement.GetString() |> nonNull

    let private tags =
        DeliveryTokens.outcomes |> List.map (fun (tag, token) -> token, tag)

    let private attempts =
        [
            yield "NOT_DISPATCHED", AttemptResult.NotDispatched
            yield "DISPATCHED_WITHOUT_REPLY", AttemptResult.DispatchedWithoutReply
            yield "HOST/NONE", AttemptResult.HostFailure None
            yield "HOST/NOT_STARTED", AttemptResult.HostFailure(Some ExecutionPhase.NotStarted)
            yield
                "HOST/STARTED_UNCONFIRMED",
                AttemptResult.HostFailure(Some ExecutionPhase.StartedUnconfirmed)
            for name, tag in tags do
                yield "OUTCOME/" + name, AttemptResult.Outcome(tag, None)

                yield
                    "OUTCOME/" + name + "/CONFLICT",
                    AttemptResult.Outcome(tag, Some "OPERATION_CONTENT_CONFLICT")
        ]

    let private knowledge =
        [
            AttemptKnowledge.NotSent
            AttemptKnowledge.ReadSucceeded
            AttemptKnowledge.ReadFailed
            AttemptKnowledge.Accepted
            AttemptKnowledge.NotAccepted
            AttemptKnowledge.Unconfirmed
        ]

    let private accumulatedToken =
        function
        | AccumulatedKnowledge.ContentExcluded -> "CONTENT_EXCLUDED"
        | AccumulatedKnowledge.Known value -> token Frames.knowledge value

    let render () =
        let classifications =
            [
                for endpoint in Endpoint.all do
                    for name, attempt in attempts do
                        yield
                            {|
                                key = Endpoint.id endpoint + "/" + name
                                knowledge =
                                    Delivery.knowledge endpoint attempt |> token Frames.knowledge
                                evidence =
                                    Delivery.evidence endpoint attempt |> token Frames.evidence
                            |}
            ]

        let accumulation =
            [
                for prior in
                    AccumulatedKnowledge.ContentExcluded
                    :: (knowledge |> List.map AccumulatedKnowledge.Known) do
                    for attempt in knowledge do
                        for evidence in
                            [
                                ResolutionEvidence.NoEvidence
                                ResolutionEvidence.ContentAccepted
                                ResolutionEvidence.ContentExcluded
                            ] do
                            yield
                                {|
                                    key =
                                        accumulatedToken prior
                                        + "/"
                                        + token Frames.knowledge attempt
                                        + "/"
                                        + token Frames.evidence evidence
                                    result =
                                        Delivery.accumulate prior attempt evidence
                                        |> accumulatedToken
                                |}
            ]

        JsonSerializer.SerializeToUtf8Bytes
            {|
                classifications = classifications
                accumulation = accumulation
            |}

    let correlation () =
        CorrelationBindings.all
        |> List.map (fun rule ->
            {|
                endpoint = Endpoint.id rule.Endpoint
                outcome = rule.Outcome
                scope = rule.Scope
                bindings =
                    rule.Bindings
                    |> List.map (fun binding ->
                        {|
                            request = binding.RequestPath
                            reply = binding.ReplyPath
                            comparison =
                                match binding.Comparison with
                                | CorrelationComparison.Exact -> "EXACT"
                                | CorrelationComparison.AsciiReference -> "ASCII_REFERENCE"
                                | CorrelationComparison.Successor -> "SUCCESSOR"
                        |})
            |})
        |> JsonSerializer.SerializeToUtf8Bytes
