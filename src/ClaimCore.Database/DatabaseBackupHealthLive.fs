namespace ClaimCore.Database

open System.Threading
open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness

module internal DatabaseBackupHealthLive =
    let private source () =
        let witnessAudit =
            DatabaseWitnessInputs.witnessAuditConnection ()
            |> Result.defaultWith (fun _ -> invalidOp "Backup health witness audit is unavailable.")

        let witnessOwner =
            DatabaseWitnessInputs.witnessOwnerConnection ()
            |> Result.defaultWith (fun _ -> invalidOp "Backup health witness owner is unavailable.")

        let custody =
            DatabaseWitnessInputs.keyRing ()
            |> Result.defaultWith (fun _ ->
                invalidOp "Backup health witness key custody is unavailable.")

        witnessAudit, witnessOwner, custody

    let private databaseNow (owner: NpgsqlConnection) (transaction: NpgsqlTransaction) =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", owner, transaction)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as value -> value.ToUniversalTime()
        | :? DateTime as value -> DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
        | _ -> invalidOp "Backup health database clock is unavailable."

    let private witnessAncestors (witness: WitnessProtocol) (claim: BackupHealthClaims) =
        task {
            if claim.Checkpoint.Sequence < 1L || claim.TestRestore.WitnessCutoff < 1L then
                invalidOp "Backup health lacks a surviving checkpoint or restored cutoff."

            for sequence, expected in
                [
                    claim.WitnessTipSequence, claim.WitnessTipHash
                    claim.Checkpoint.Sequence, claim.Checkpoint.Hash
                    claim.TestRestore.WitnessCutoff, claim.TestRestore.WitnessCutoffHash
                ] do
                let! observed = witness.TryReadHashAtSequence(sequence, CancellationToken.None)

                match observed with
                | Some hash when Convert.ToHexStringLower(hash) = expected -> ()
                | _ -> invalidOp "Backup health witness ancestor differs."
        }

    let private signer
        (owner: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (policy: BackupHealthPolicy)
        (claim: BackupHealthClaims)
        (loaded: LoadedBackupHealthEvidence)
        =
        let publicKey, holder =
            DatabaseRestoreSignedEvidence.signer
                owner
                transaction
                claim.SignerKeyId
                CopySignerPurpose.Checkpoint

        try
            let separate =
                policy.Roles
                |> List.forall (fun role ->
                    not (
                        CryptographicOperations.FixedTimeEquals(
                            publicKey.AsSpan(),
                            role.PublicKey.AsSpan()
                        )
                    )
                    && holder <> role.SignerHolderActorId
                    && holder <> role.AdminActorId)

            if
                holder <> claim.SignerHolderActorId
                || not separate
                || not (
                    ManagedCopySignature.verify
                        publicKey
                        loaded.CertificateBytes
                        loaded.CertificateSignature
                )
            then
                invalidOp "Backup health issuer lacks independent current CHECKPOINT custody."
        finally
            CryptographicOperations.ZeroMemory(publicKey.AsSpan())

    let private matchingPair
        (facts: RestoredPairFacts)
        (claim: BackupHealthClaims)
        (evidence: BackupHealthEvidence)
        =
        facts.InstallationId = claim.InstallationId
        && facts.LineageId = claim.LineageId
        && facts.Epoch = claim.Epoch
        && facts.WriterGeneration = claim.WriterGeneration
        && facts.AuthorityRevision = claim.AuthorityRevision
        && facts.PrimarySystemId = claim.PrimarySystemId
        && facts.PrimaryTimeline = claim.PrimaryTimeline
        && facts.WitnessSystemId = claim.WitnessSystemId
        && facts.WitnessTimeline = claim.WitnessTimeline
        && facts.ManagedCopySnapshotSha256 = claim.KnownCopyInventorySha256
        && facts.WitnessCutoff >= claim.WitnessTipSequence
        && facts.PendingIntents = 0L
        && facts.InstallationId = evidence.InstallationId

    let private inspectCurrent
        policy
        evidence
        loaded
        witnessOwner
        owner
        transaction
        witness
        summary
        tip
        =
        task {
            let now = databaseNow owner transaction

            let claim =
                BackupHealthCertificate.parse loaded.CertificateBytes now
                |> Option.defaultWith (fun () ->
                    invalidOp "Signed backup health certificate is invalid or stale.")

            let! facts =
                DatabaseRestoreLive.inspect owner transaction witnessOwner witness summary tip

            if not (matchingPair facts claim evidence) then
                invalidOp "Backup health current pair differs from signed source."

            DatabaseBackupHealthBinding.verify policy evidence claim loaded.SourceBytes now

            DatabaseBackupHealthWalCoverage.verify evidence
            DatabaseBackupHealthWriterFence.verify owner transaction claim

            DatabaseBackupHealthCopyRows.verify owner transaction policy evidence now

            do! witnessAncestors witness claim
            signer owner transaction policy claim loaded
            return claim, now
        }

    let verify
        ownerConnection
        (policy: BackupHealthPolicy)
        (evidence: BackupHealthEvidence)
        (loaded: LoadedBackupHealthEvidence)
        =
        task {
            let witnessAudit, witnessOwner, custody = source ()
            use custody = custody

            let suppressionPath =
                Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE")
                |> Option.ofObj
                |> Option.defaultWith (fun () ->
                    invalidOp "Backup health suppression key is unavailable.")

            use suppression = SuppressionKeyFile.Load(suppressionPath)

            let! _, _, qualified =
                DatabaseVerifyData.auditedRestoredWith
                    ownerConnection
                    witnessAudit
                    custody
                    suppression
                    (inspectCurrent policy evidence loaded witnessOwner)

            return qualified
        }
