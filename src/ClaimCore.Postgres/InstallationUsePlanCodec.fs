namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Text.Json

/// Exact data-minimal owner plan, readable by HUMAN OWNER reviewers but never a private path.
module internal InstallationUsePlanCodec =
    let private names =
        ("backupCaptureHash|backupCaptureSequence|captureNonce|"
         + "captureReceiptSha256|checkpointHash|checkpointObjectSha256|checkpointSequence|"
         + "cycleId|epoch|format|installationId|leaseId|lineageId|minimumArtifactCutoffSequence|"
         + "minimumPrimaryWalHorizon|minimumWitnessWalHorizon|policySha256|"
         + "primaryBaseCiphertextSha256|primaryBaseCopyId|primaryBasePhysicalReceiptSha256|"
         + "primaryBaseWalHorizon|primaryWalSegmentBytes|"
         + "primaryBaseRevision|primarySystemId|primaryTimeline|publicationRootKeySha256|"
         + "testRestoreFullAuditSha256|testRestoreReportSha256|testRestoreWitnessCutoff|"
         + "testRestoreWitnessCutoffHash|witnessBaseCiphertextSha256|witnessBaseCopyId|"
         + "witnessBasePhysicalReceiptSha256|witnessBaseRevision|witnessBaseWalHorizon|"
         + "witnessSystemId|witnessTimeline|witnessWalSegmentBytes|writerGeneration")
            .Split('|')
        |> Array.toList
        |> List.sort

    let private text (name: string) (root: JsonElement) =
        root.GetProperty(name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Activation plan text is absent.")

    let private digest name root =
        let raw = text name root
        let bytes = Convert.FromHexString(raw)

        if bytes.Length <> 32 || Convert.ToHexStringLower(bytes) <> raw then
            invalidOp "Activation plan digest is invalid."

        raw

    let private id name root =
        let raw = text name root
        let parsed = Guid.ParseExact(raw, "D")

        if parsed = Guid.Empty || parsed.ToString("D") <> raw then
            invalidOp "Activation plan identity is invalid."

        parsed

    let private decodePlan canonical root : BackupHealthActivationPlan =
        {
            Canonical = Array.copy canonical
            PlanSha256 = SHA256.HashData(canonical) |> Convert.ToHexStringLower
            InstallationId = id "installationId" root
            LineageId = id "lineageId" root
            Epoch = root.GetProperty("epoch").GetInt64()
            WriterGeneration = root.GetProperty("writerGeneration").GetInt64()
            PolicySha256 = digest "policySha256" root
            PublicationRootSha256 = digest "publicationRootKeySha256" root
            CycleId = id "cycleId" root
            LeaseId = id "leaseId" root
            CaptureReceiptSha256 = digest "captureReceiptSha256" root
            PrimaryBaseCopyId = id "primaryBaseCopyId" root
            WitnessBaseCopyId = id "witnessBaseCopyId" root
            PrimaryBasePhysicalReceiptSha256 = digest "primaryBasePhysicalReceiptSha256" root
            WitnessBasePhysicalReceiptSha256 = digest "witnessBasePhysicalReceiptSha256" root
            PrimaryBaseWalHorizon = text "primaryBaseWalHorizon" root
            WitnessBaseWalHorizon = text "witnessBaseWalHorizon" root
            PrimaryWalSegmentBytes = root.GetProperty("primaryWalSegmentBytes").GetInt32()
            WitnessWalSegmentBytes = root.GetProperty("witnessWalSegmentBytes").GetInt32()
            CheckpointObjectSha256 = digest "checkpointObjectSha256" root
            TestRestoreReportSha256 = digest "testRestoreReportSha256" root
            TestRestoreFullAuditSha256 = digest "testRestoreFullAuditSha256" root
            MinimumArtifactCutoffSequence =
                root.GetProperty("minimumArtifactCutoffSequence").GetInt64()
            MinimumPrimaryWalHorizon = text "minimumPrimaryWalHorizon" root
            MinimumWitnessWalHorizon = text "minimumWitnessWalHorizon" root
        }

    let parse (canonical: byte array) =
        try
            if
                canonical.Length < 2
                || canonical.Length > 16384
                || canonical[canonical.Length - 1] <> byte '\n'
                || canonical[0 .. canonical.Length - 2]
                   |> Array.exists (fun value -> value < 32uy || value > 126uy)
            then
                None
            else
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
                let root = document.RootElement

                if
                    (root.EnumerateObject() |> Seq.map _.Name |> Seq.toList) <> names
                    || text "format" root <> "claimcore-real-data-activation-plan-1"
                then
                    None
                else
                    let result = decodePlan canonical root

                    if
                        result.Epoch < 1L
                        || result.WriterGeneration < 1L
                        || result.MinimumArtifactCutoffSequence < 0L
                        || result.PrimaryBaseCopyId = result.WitnessBaseCopyId
                    then
                        None
                    else
                        Some result
        with _ ->
            None
