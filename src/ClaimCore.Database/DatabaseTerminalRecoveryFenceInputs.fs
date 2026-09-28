namespace ClaimCore.Database

open System
open System.IO
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// Historical signed W2 evidence is retained outside managed backup copies. The same exact
/// owner-private package is required after those copies are verified absent.
module internal DatabaseTerminalRecoveryFenceInputs =
    let private directoryName = "CLAIMCORE_TERMINAL_FENCE_EVIDENCE_DIR"

    let private paths directory =
        {
            Report = Path.Combine(directory, "report.json")
            ReportSignature = Path.Combine(directory, "report.sig")
            EvidenceIndex = Path.Combine(directory, "index.json")
            Fence = Path.Combine(directory, "fence.json")
            FenceSignature = Path.Combine(directory, "fence.sig")
            Supplement = Path.Combine(directory, "supplement.json")
            SupplementSignature = Path.Combine(directory, "supplement.sig")
        }

    let verifiedCandidate ownerConnection : WriterActivationEvidence option =
        try
            let directory =
                Environment.GetEnvironmentVariable(directoryName)
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.defaultWith (fun () ->
                    invalidOp "Owner-private recovery-fence evidence is unavailable.")

            match PrivateFileService.requirePrivateDirectory directory with
            | Error _ -> None
            | Ok() ->
                DatabaseFencedTailHistoricalOwner.tryCandidate ownerConnection (paths directory)
                |> Option.map fst
        with _ ->
            None
