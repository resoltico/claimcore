namespace ClaimCore.Database

open System

/// Closed owner-only invocation syntax for terminal installation loss.
module internal DatabaseLossArguments =
    let private exactGuid (value: string) =
        match Guid.TryParseExact(value, "D") with
        | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = value -> Some parsed
        | _ -> None

    let private decisionPaths candidate evidence checkpoint known first second =
        {
            Candidate = candidate
            EvidenceReport = evidence
            IndependentCheckpoint = checkpoint
            KnownOperations = known
            SignatureOne = first
            SignatureTwo = second
        }

    let parse =
        function
        | [ "draft-installation-loss-retirement"
            keyOne
            keyTwo
            evidence
            checkpoint
            known
            mode
            output ] ->
            match exactGuid keyOne, exactGuid keyTwo with
            | Some first, Some second when
                first <> second && (mode = "KNOWN_OPERATIONS" || mode = "UNKNOWN_OPERATIONS")
                ->
                Some(
                    Ok(
                        DatabaseCommand.DraftInstallationLossRetirement(
                            first,
                            second,
                            evidence,
                            checkpoint,
                            known,
                            mode,
                            output
                        )
                    )
                )
            | _ -> Some(Error DatabaseInputProblem.UnsupportedInvocation)
        | [ "retire-installation-after-loss"; candidate; evidence; checkpoint; known; first; second ] ->
            decisionPaths candidate evidence checkpoint known first second
            |> DatabaseCommand.RetireInstallationAfterLoss
            |> Ok
            |> Some
        | [ "reconcile-installation-loss-retirement"
            candidate
            evidence
            checkpoint
            known
            first
            second ] ->
            decisionPaths candidate evidence checkpoint known first second
            |> DatabaseCommand.ReconcileInstallationLossRetirement
            |> Ok
            |> Some
        | _ -> None
