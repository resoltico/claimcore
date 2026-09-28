namespace ClaimCore.Postgres

open System
open System.Data
open Npgsql

/// The real-data mutation gate reopens short-lived signed evidence at the database clock,
/// then compares the current witnessed lineage and complete copy-inventory projection.
module internal BackupHealthRuntimeAdmission =
    let private databaseNow (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as value -> value.ToUniversalTime()
        | :? DateTime as value -> DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
        | _ -> invalidOp "Backup health database clock is unavailable."

    let private currentAge now maximum observed =
        observed <= now && now - observed <= TimeSpan.FromSeconds(float maximum)

    let private policyIdentity (policy: BackupHealthPolicy) (claim: BackupHealthClaims) =
        claim.PolicyId = policy.PolicyId
        && claim.MaximumBackupAgeSeconds = policy.MaximumBackupAgeSeconds
        && claim.MaximumWalLagSeconds = policy.MaximumWalLagSeconds
        && claim.MaximumCheckpointAgeSeconds = policy.MaximumCheckpointAgeSeconds
        && claim.MaximumRestoreTestAgeSeconds = policy.MaximumRestoreTestAgeSeconds
        && claim.RestoreHorizonSeconds = policy.RestoreHorizonSeconds

    let private policyBound (policy: BackupHealthPolicy) (claim: BackupHealthClaims) now =
        if
            not (policyIdentity policy claim)
            || claim.ValidUntil > claim.CheckedAt.AddSeconds(90.)
            || not (currentAge now policy.MaximumBackupAgeSeconds claim.PrimaryBase.VerifiedAt)
            || not (currentAge now policy.MaximumBackupAgeSeconds claim.WitnessBase.VerifiedAt)
            || not (currentAge now policy.MaximumWalLagSeconds claim.PrimaryWal.VerifiedAt)
            || not (currentAge now policy.MaximumWalLagSeconds claim.WitnessWal.VerifiedAt)
            || not (currentAge now policy.MaximumCheckpointAgeSeconds claim.Checkpoint.VerifiedAt)
            || not (currentAge now policy.MaximumRestoreTestAgeSeconds claim.TestRestore.VerifiedAt)
        then
            invalidOp "Backup health policy or freshness diverges."

    let private witnessBound (witness: WitnessProtocol) (claim: BackupHealthClaims) =
        let lease = witness.AcquireReadFence(claim.WriterGeneration)

        try
            let snapshot = witness.Snapshot()

            if
                snapshot.Identity.InstallationId <> claim.InstallationId
                || snapshot.Identity.LineageId <> claim.LineageId
                || snapshot.Identity.Epoch <> claim.Epoch
                || snapshot.WriterGeneration <> claim.WriterGeneration
                || snapshot.HandoffPending
                || snapshot.ActivationPending
                || snapshot.TipSequence < claim.WitnessTipSequence
            then
                invalidOp "Backup health witness authority is unavailable."

            let require sequence digest =
                match witness.TryReadHashAtSequence(sequence) with
                | Some hash when Convert.ToHexStringLower(hash) = digest -> ()
                | _ -> invalidOp "Backup health witness ancestor changed."

            require claim.WitnessTipSequence claim.WitnessTipHash
            require claim.Checkpoint.Sequence claim.Checkpoint.Hash
            require claim.TestRestore.WitnessCutoff claim.TestRestore.WitnessCutoffHash
            lease
        with _ ->
            lease.Dispose()
            reraise ()

    let verifyLocked
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (profile: ReviewedDeploymentProfile)
        (policyBytes: byte array)
        (canonical: byte array)
        (signature: byte array)
        =
        let policy =
            BackupHealthPolicyCodec.parse policyBytes profile.BackupHealthPolicySha256
            |> Option.defaultWith (fun () -> invalidOp "Reviewed backup health policy is absent.")

        let now = databaseNow connection transaction

        let claim =
            BackupHealthCertificate.parse canonical now
            |> Option.defaultWith (fun () ->
                invalidOp "Signed backup health is expired or invalid.")

        policyBound policy claim now
        use _witnessLease = witnessBound witness claim
        BackupHealthRuntimeAuthority.verifyLineage connection transaction claim
        BackupHealthRuntimeAuthority.verifyRevision connection transaction claim
        BackupHealthRuntimeAuthority.verifySigner connection transaction claim canonical signature
        let _, inventorySha = ManagedCopyInventoryDigest.compute connection transaction

        if inventorySha <> claim.KnownCopyInventorySha256 then
            invalidOp "Backup health known copy inventory changed."

        let copies = BackupHealthRuntimeCopies.verify connection transaction claim now
        BackupHealthRuntimeWalCoverage.verify claim copies

    let verify
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (profile: ReviewedDeploymentProfile)
        (policyBytes: byte array)
        (canonical: byte array)
        (signature: byte array)
        =
        use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)
        verifyLocked connection transaction witness profile policyBytes canonical signature
        transaction.Rollback()
