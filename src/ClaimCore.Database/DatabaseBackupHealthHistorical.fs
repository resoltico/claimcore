namespace ClaimCore.Database

open System.Threading
open System
open System.Data
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness

/// Historical authenticity is deliberately separate from fresh issuer qualification.
/// It cannot assert that the archive, copy inventory or certificate is healthy now.
module internal DatabaseBackupHealthHistorical =
    let private databaseNow (owner: NpgsqlConnection) =
        use command = new NpgsqlCommand("SELECT clock_timestamp()", owner)

        match command.ExecuteScalar() with
        | :? DateTimeOffset as value -> value.ToUniversalTime()
        | :? DateTime as value -> DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero)
        | _ -> invalidOp "Historical backup health database clock is unavailable."

    let verifiedDocuments
        (profile: ReviewedDeploymentProfile)
        (loaded: LoadedBackupHealthEvidence)
        dbNow
        =
        let policy =
            BackupHealthPolicyCodec.parse loaded.PolicyBytes profile.BackupHealthPolicySha256
            |> Option.defaultWith (fun () -> invalidOp "Reviewed historical health policy differs.")

        let source =
            DatabaseBackupHealthIndependentProof.verifyHistoricalSignatures policy loaded dbNow

        let claim =
            BackupHealthCertificate.parseHistorical loaded.CertificateBytes dbNow
            |> Option.defaultWith (fun () ->
                invalidOp "Historical signed health certificate is invalid.")

        DatabaseBackupHealthBinding.verify policy source claim loaded.SourceBytes claim.CheckedAt
        DatabaseBackupHealthWalCoverage.verify source
        policy, source, claim

    let private witnessFor (identity: Identity) =
        let auditConnection =
            DatabaseWitnessInputs.witnessAuditConnection ()
            |> Result.defaultWith (fun _ ->
                invalidOp "Historical backup health witness input is unavailable.")

        let custody =
            DatabaseWitnessInputs.keyRing ()
            |> Result.defaultWith (fun _ ->
                invalidOp "Historical backup health key custody is unavailable.")

        try
            let store = Store.OpenAudit(auditConnection, identity)

            try
                new WitnessProtocol(store, custody, identity)
            with _ ->
                (store :> IDisposable).Dispose()
                reraise ()
        with _ ->
            custody.Dispose()
            reraise ()

    let private ancestor (witness: WitnessProtocol) sequence expected =
        task {
            let! observed = witness.TryReadHashAtSequence(sequence, CancellationToken.None)

            match observed with
            | Some hash when Convert.ToHexStringLower(hash) = expected -> ()
            | _ -> invalidOp "Historical backup health witness ancestor differs."
        }

    let verifyIssuer
        publicKey
        holder
        (claim: BackupHealthClaims)
        (loaded: LoadedBackupHealthEvidence)
        =
        if
            holder <> claim.SignerHolderActorId
            || not (
                ManagedCopySignature.verify
                    publicKey
                    loaded.CertificateBytes
                    loaded.CertificateSignature
            )
        then
            invalidOp "Historical backup health issuer signature differs."

    let verify ownerConnection profile loaded =
        task {
            let builder = OwnerConnection.builder ownerConnection
            use owner = new NpgsqlConnection(builder.ConnectionString)
            owner.Open()
            OwnerConnection.requireIdentity owner
            SchemaBaseline.requireCurrent owner
            let identity, _, _ = DatabaseVerifyData.identity owner
            let _, source, claim = verifiedDocuments profile loaded (databaseNow owner)

            if
                claim.InstallationId <> identity.InstallationId
                || claim.LineageId <> identity.LineageId
                || claim.Epoch <> identity.Epoch
                || source.InstallationId <> identity.InstallationId
                || source.LineageId <> identity.LineageId
                || source.Epoch <> identity.Epoch
                || claim.Checkpoint.Sequence < 1L
                || claim.TestRestore.WitnessCutoff < 1L
            then
                invalidOp "Historical backup health installation differs."

            use witness = witnessFor identity
            do! witness.AdmitReadOnly(CancellationToken.None)
            do! ancestor witness claim.WitnessTipSequence claim.WitnessTipHash
            do! ancestor witness claim.Checkpoint.Sequence claim.Checkpoint.Hash
            do! ancestor witness claim.TestRestore.WitnessCutoff claim.TestRestore.WitnessCutoffHash
            use transaction = owner.BeginTransaction(IsolationLevel.RepeatableRead)

            let! key, holder =
                DatabaseRestoreSignedEvidence.historicalSigner
                    owner
                    transaction
                    witness
                    claim.SignerKeyId
                    CopySignerPurpose.Checkpoint
                    claim.Epoch
                    claim.WitnessTipSequence

            try
                verifyIssuer key holder claim loaded
            finally
                CryptographicOperations.ZeroMemory(key)

            transaction.Rollback()
            return SHA256.HashData(loaded.CertificateBytes) |> Convert.ToHexStringLower
        }
