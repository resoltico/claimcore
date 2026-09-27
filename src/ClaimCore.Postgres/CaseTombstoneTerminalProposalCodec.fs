namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application

/// Exact canonical owner-draft decoding for retry and full audit; no familiar tag or partial
/// JSON object can turn arbitrary stored bytes into terminal authority.
module internal CaseTombstoneTerminalProposalCodec =
    let private commonNames =
        [|
            "version"
            "kind"
            "eventId"
            "caseId"
            "expectedAuthorityRevision"
            "expectedAuthorityHash"
            "installationId"
            "lineageId"
            "witnessEpoch"
            "pruneEventId"
            "witnessCutoffSequence"
            "witnessCutoffHash"
            "copyInventoryDigest"
            "relevantCopyCount"
            "expectedWriterGeneration"
            "policyId"
            "suppressionUntil"
            "validUntil"
        |]

    let private finalNames =
        Array.append
            commonNames
            [| "recoveryFenceDigest"; "oldWriterGeneration"; "newWriterGeneration" |]

    let private string (root: JsonElement) (name: string) =
        root.GetProperty(name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Terminal proposal text is null.")

    let private time (root: JsonElement) (name: string) =
        DateTimeOffset.ParseExact(
            string root name,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None
        )

    let private common (root: JsonElement) =
        {
            EventId = root.GetProperty("eventId").GetGuid()
            CaseId = root.GetProperty("caseId").GetGuid()
            ExpectedAuthorityRevision = root.GetProperty("expectedAuthorityRevision").GetInt64()
            ExpectedAuthorityHash = string root "expectedAuthorityHash"
            InstallationId = root.GetProperty("installationId").GetGuid()
            LineageId = root.GetProperty("lineageId").GetGuid()
            WitnessEpoch = root.GetProperty("witnessEpoch").GetInt64()
            PruneEventId = root.GetProperty("pruneEventId").GetGuid()
            WitnessCutoffSequence = root.GetProperty("witnessCutoffSequence").GetInt64()
            WitnessCutoffHash = string root "witnessCutoffHash"
            CopyInventoryDigest = string root "copyInventoryDigest"
            RelevantCopyCount = root.GetProperty("relevantCopyCount").GetInt64()
            ExpectedWriterGeneration = root.GetProperty("expectedWriterGeneration").GetInt64()
            PolicyId = string root "policyId"
            SuppressionUntil = time root "suppressionUntil"
            ValidUntil = time root "validUntil"
        }

    let private value (root: JsonElement) =
        let fields = root.EnumerateObject() |> Seq.map _.Name |> Seq.toArray

        if root.GetProperty("version").GetInt32() <> 1 then
            None
        else
            match string root "kind" with
            | "CONFIRM_MANAGED_PAYLOAD_ABSENCE" when fields = commonNames ->
                Some(TombstoneTerminalProposal.ConfirmManagedPayloadAbsence(common root))
            | "COMPLETE_SUPPRESSION_HORIZON" when fields = finalNames ->
                Some(
                    TombstoneTerminalProposal.CompleteSuppressionHorizon
                        {
                            Copy = common root
                            RecoveryFenceDigest = string root "recoveryFenceDigest"
                            OldWriterGeneration = root.GetProperty("oldWriterGeneration").GetInt64()
                            NewWriterGeneration = root.GetProperty("newWriterGeneration").GetInt64()
                        }
                )
            | _ -> None

    let decode (bytes: byte array) =
        if bytes.Length < 2 || bytes.Length > 8192 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))

                match value document.RootElement with
                | None -> None
                | Some proposal ->
                    let rebuilt = CaseTombstoneTerminalCandidate.proposal proposal

                    try
                        if rebuilt = bytes then Some proposal else None
                    finally
                        CryptographicOperations.ZeroMemory(rebuilt)
            with _ ->
                None
