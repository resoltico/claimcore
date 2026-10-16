namespace ClaimCore.ClientProtocol

/// A finite protocol policy, projected unchanged into native and browser clients.
module DeliveryPolicy =
    let outcomePolicies =
        [
            "ACCEPTED", true, "ACCEPTED"
            "ALREADY_ACCEPTED", true, "ACCEPTED"
            "ALREADY_ACCEPTED", false, "ACCEPTED"
            "SUCCEEDED", false, "READ_SUCCEEDED"
            "REVIEWED", false, "READ_SUCCEEDED"
            "REJECTED", false, "READ_FAILED"
            "FAILED", false, "READ_FAILED"
            "CANCELLED", false, "READ_FAILED"
            "REJECTED", true, "NOT_ACCEPTED"
            "FAILED", true, "NOT_ACCEPTED"
            "CANCELLED", true, "NOT_ACCEPTED"
            "OUTCOME_UNKNOWN", true, "UNCONFIRMED"
        ]

    let missingPolicies =
        [
            false, false, "NOT_SENT"
            false, true, "NOT_SENT"
            true, false, "READ_FAILED"
            true, true, "UNCONFIRMED"
        ]

    let hostPolicies =
        [
            false, true, "READ_FAILED"
            false, false, "READ_FAILED"
            true, true, "NOT_ACCEPTED"
            true, false, "UNCONFIRMED"
        ]

    let contentExcludedDiagnostic = "OPERATION_CONTENT_CONFLICT"

    let evidencePolicies =
        [
            "ACCEPTED", "*", "*", "CONTENT_ACCEPTED"
            "*", "REJECTED", contentExcludedDiagnostic, "CONTENT_EXCLUDED"
        ]

    let resolutionEvidence submission knowledge tag diagnostic =
        if not submission then
            "NONE"
        else
            let matches expected actual = expected = "*" || expected = actual

            evidencePolicies
            |> List.tryPick (fun (expectedKnowledge, expectedTag, expectedDiagnostic, evidence) ->
                if
                    matches expectedKnowledge knowledge
                    && matches expectedTag tag
                    && matches expectedDiagnostic diagnostic
                then
                    Some evidence
                else
                    None)
            |> Option.defaultValue "NONE"

    let unresolvedKnowledge = "UNCONFIRMED"

    let resolutionPolicies =
        [ "CONTENT_ACCEPTED", "ACCEPTED"; "CONTENT_EXCLUDED", "CONTENT_EXCLUDED" ]

    let outcome mutates tag =
        outcomePolicies
        |> List.tryPick (fun (value, mutation, knowledge) ->
            if value = tag && mutation = mutates then
                Some knowledge
            else
                None)
        |> Option.defaultWith (fun () -> invalidArg "tag" "Outcome has no delivery policy.")

    let missing dispatched mutates =
        missingPolicies
        |> List.find (fun (sent, mutation, _) -> sent = dispatched && mutation = mutates)
        |> fun (_, _, knowledge) -> knowledge

    let host mutates phase =
        hostPolicies
        |> List.find (fun (mutation, notStarted, _) ->
            mutation = mutates && notStarted = (phase = Some "NOT_STARTED"))
        |> fun (_, _, knowledge) -> knowledge

    let accumulate prior attempt evidence =
        resolutionPolicies
        |> List.tryFind (fun (value, _) -> value = evidence)
        |> Option.map snd
        |> Option.defaultWith (fun () -> if prior = unresolvedKnowledge then prior else attempt)
