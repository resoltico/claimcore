namespace ClaimCore.Database

open System
open System.IO
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

/// Produces a signed synthetic restored-pair report only after two complete live audits:
/// one to derive the facts, and a second independent consumer recheck of the exact bytes.
module internal DatabaseRestoreProduceLive =
    let private readOwners (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
        use command =
            new NpgsqlCommand(
                "SELECT a.actor_id,g.changed_revision,e.event_id "
                + "FROM claimcore.actors a JOIN claimcore.actor_grants g ON g.actor_id=a.actor_id "
                + "JOIN claimcore.actor_authority_events e ON e.revision=g.changed_revision "
                + "AND e.target_actor_id=a.actor_id AND e.action_name IN "
                + "('PROVISION_INITIAL_OWNER','GRANT_ROLE') "
                + "WHERE a.enabled AND a.principal_kind='HUMAN' AND g.active "
                + "AND g.scope_kind='INSTALLATION' "
                + "AND g.scope_case_id='00000000-0000-0000-0000-000000000000' "
                + "AND g.role_name='OWNER' ORDER BY a.actor_id",
                connection,
                transaction
            )

        use reader = command.ExecuteReader()
        let rows = ResizeArray<RestoreOwnerApprover>()

        while reader.Read() do
            if rows.Count >= 1000 then
                invalidOp "Restored owner roster exceeds the reviewed bound."

            rows.Add(
                {
                    ActorId = reader.GetGuid(0)
                    GrantRevision = reader.GetInt64(1)
                    ApprovalEventId = reader.GetGuid(2)
                }
            )

        if rows.Count < 2 then
            invalidOp "Restored pair lacks two witnessed human owners."

        rows |> Seq.toList

    let private fileDigest maximum path =
        match PrivateFileService.hashPrivateFile maximum path with
        | Ok(length, hash) when length > 0L -> length, Convert.ToHexStringLower(hash)
        | _ -> invalidOp "Owner-private restore evidence is missing or unsafe."

    let private binaryDigest () =
        let path = typeof<DatabaseCommand>.Assembly.Location

        if String.IsNullOrWhiteSpace path then
            invalidOp "Verifier binary location is unavailable."

        use source = File.OpenRead(path)
        SHA256.HashData(source) |> Convert.ToHexStringLower

    let private sourceDigests (input: RestoreProduceInput) =
        let checkpointBytes, checkpointSha = fileDigest 16384L input.Index.CheckpointFile
        let _, barrierSha = fileDigest 32768L input.Index.BarrierFile
        let _, inventorySha = fileDigest 131072L input.Index.InventorySnapshotFile
        let _, manifestSha = fileDigest 131072L input.Index.ManifestFile

        if
            manifestSha <> input.Index.ManifestSha256
            || checkpointSha = String.replicate 64 "0"
        then
            invalidOp "Signed restore source evidence changed."

        checkpointBytes, checkpointSha, barrierSha, inventorySha

    let private claims
        (input: RestoreProduceInput)
        (facts: RestoredPairFacts)
        owners
        checkedAt
        checkpointPublicSha
        =
        let checkpointBytes, checkpointSha, barrierSha, inventorySha = sourceDigests input

        {
            Scope = "synthetic-only"
            InstallationId = facts.InstallationId
            LineageId = facts.LineageId
            Epoch = facts.Epoch
            CycleId = input.Index.CycleId
            BackupCaptureSequence = input.BackupCaptureSequence
            BackupCaptureHash = input.BackupCaptureHash
            WitnessCutoff = facts.WitnessCutoff
            WitnessCutoffHash = facts.WitnessCutoffHash
            PrimarySystemId = facts.PrimarySystemId
            PrimaryTimeline = facts.PrimaryTimeline
            WitnessSystemId = facts.WitnessSystemId
            WitnessTimeline = facts.WitnessTimeline
            PrimaryRegisteredWalHorizon = input.Index.PrimaryRegisteredWalHorizon
            WitnessRegisteredWalHorizon = input.Index.WitnessRegisteredWalHorizon
            SignerKeyId = input.Publication.ReportSignerKeyId
            VerifierBinarySha256 = binaryDigest ()
            EvidenceIndexSha256 = String.replicate 64 "0"
            CheckpointSha256 = checkpointSha
            SignedInventoryFileSha256 = inventorySha
            QuiescentBarrierSha256 = barrierSha
            CatalogManifestSha256 = facts.CatalogManifestSha256
            AuthorityRevision = facts.AuthorityRevision
            AuthorizedApprovers = owners
            ArchiveCustody = DatabaseRestoreProduceCustody.archive input
            CheckpointCustody =
                DatabaseRestoreProduceCustody.checkpoint input checkpointBytes checkpointSha
            CustodyKeyId = input.CustodyKeyId
            CustodyPublicKeySha256 = checkpointPublicSha
            CheckedAt = checkedAt
            ValidUntil = input.ValidUntil
        }

    let private witnessedSigners
        (input: RestoreProduceInput)
        (barrier: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        facts
        =
        let reportKey, reportHolder =
            DatabaseRestoreSignedEvidence.signer
                barrier
                transaction
                input.Publication.ReportSignerKeyId
                CopySignerPurpose.RestoreReport

        let checkpointKey, checkpointHolder =
            DatabaseRestoreSignedEvidence.signer
                barrier
                transaction
                input.Index.CheckpointSignerKeyId
                CopySignerPurpose.Checkpoint

        try
            try
                if
                    reportHolder = checkpointHolder
                    || CryptographicOperations.FixedTimeEquals(reportKey, checkpointKey)
                    || input.CustodyKeyId <> input.Index.CheckpointSignerKeyId
                then
                    invalidOp "Restore report and checkpoint custody are not independent."

                let owners = readOwners barrier transaction
                DatabaseRestoreOwnerRoster.verify barrier transaction owners
                let checkpointPublicSha = SHA256.HashData(checkpointKey) |> Convert.ToHexStringLower
                facts, owners, reportKey, checkpointPublicSha
            with _ ->
                CryptographicOperations.ZeroMemory(reportKey)
                reraise ()
        finally
            CryptographicOperations.ZeroMemory(checkpointKey)

    let private derive owner witnessOwner witnessAudit custody suppression input =
        task {
            let! _, _, result =
                DatabaseVerifyData.auditedRestoredWith
                    owner
                    witnessAudit
                    custody
                    suppression
                    (fun barrier transaction witness audit tip ->
                        task {
                            let! facts =
                                DatabaseRestoreLive.inspect
                                    barrier
                                    transaction
                                    witnessOwner
                                    witness
                                    audit
                                    tip

                            if facts.PendingIntents <> 0L then
                                invalidOp "A restored pair has unsettled witness authority."

                            return witnessedSigners input barrier transaction facts
                        })

            return result
        }

    let private signAndRecheck
        owner
        witnessAudit
        witnessOwner
        (custody: IKeyCustody)
        (suppression: SuppressionKeyFile)
        (input: RestoreProduceInput)
        (bound: RestoreReportClaims)
        (produced: RestoreProducedEvidence)
        reportKey
        =
        task {
            let signature = input.ReportSigner produced.Report

            if
                signature.Length <> 64
                || not (ManagedCopySignature.verify reportKey produced.Report signature)
            then
                invalidOp "Private restore report signer does not match its witnessed roster."

            let files: RestoreReportFiles =
                {
                    Report = produced.Report
                    Signature = signature
                    EvidenceIndex = produced.EvidenceIndex
                    ReportSha256 = produced.ReportSha256
                    EvidenceIndexSha256 = produced.EvidenceIndexSha256
                }

            let nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))

            let! rechecked =
                DatabaseRestoreReportRecheck.evaluateSynthetic
                    (Some input.Publication)
                    owner
                    witnessAudit
                    witnessOwner
                    custody
                    suppression
                    files
                    nonce
                    bound.VerifierBinarySha256
                    DateTimeOffset.UtcNow

            match rechecked with
            | Ok _ ->
                return
                    {
                        Evidence = produced
                        ReportSignature = signature
                    }
            | Error _ ->
                return invalidOp "Produced restored-pair evidence failed independent recheck."
        }

    let produceSynthetic
        owner
        witnessAudit
        witnessOwner
        (custody: IKeyCustody)
        (suppression: SuppressionKeyFile)
        (input: RestoreProduceInput)
        =
        task {
            DatabaseRestoreIsolation.requireIsolated owner witnessAudit

            DatabaseRestoreProduceCanonical.requireValidity input.ValidUntil

            let! facts, owners, reportKey, checkpointPublicSha =
                derive owner witnessOwner witnessAudit custody suppression input

            try
                let instant = DateTimeOffset.UtcNow

                let checkedAt =
                    DateTimeOffset(
                        instant.UtcTicks - instant.UtcTicks % TimeSpan.TicksPerSecond,
                        TimeSpan.Zero
                    )

                let unsigned = claims input facts owners checkedAt checkpointPublicSha

                let indexBytes =
                    DatabaseRestoreProduceCanonical.evidenceIndex
                        unsigned
                        input.Index
                        input.Publication

                let indexSha = SHA256.HashData(indexBytes) |> Convert.ToHexStringLower

                let bound =
                    { unsigned with
                        EvidenceIndexSha256 = indexSha
                    }

                let produced =
                    DatabaseRestoreProduceCanonical.produce bound input.Index input.Publication

                return!
                    signAndRecheck
                        owner
                        witnessAudit
                        witnessOwner
                        custody
                        suppression
                        input
                        bound
                        produced
                        reportKey
            finally
                CryptographicOperations.ZeroMemory(reportKey)
        }
