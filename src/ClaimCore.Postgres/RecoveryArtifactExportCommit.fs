namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.RecordFormat
open WitnessProtocolReconciliation

module internal RecoveryArtifactExportCommit =
    let private nonceAvailable connection transaction keyId nonce ct =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT NOT EXISTS (SELECT 1 FROM claimcore.recovery_artifact_exports "
                    + "WHERE key_id=@key AND nonce=@nonce)",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            Sql.add command "nonce" NpgsqlDbType.Bytea (box nonce)
            let! result = command.ExecuteScalarAsync(ct)
            return result :?> bool
        }

    let rec private uniqueNonce connection transaction keyId attempts ct =
        task {
            if attempts = 0 then
                invalidOp "Recovery artifact nonce reservation failed."

            let nonce = RandomNumberGenerator.GetBytes(12)
            let! available = nonceAvailable connection transaction keyId nonce ct

            if available then
                return nonce
            else
                return! uniqueNonce connection transaction keyId (attempts - 1) ct
        }

    let private artifact
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (retained: RetainedPreparation)
        (key: RecoveryArtifactIssueKey)
        exportId
        instant
        nonce
        : RecoveryArtifactV3 =
        {
            KeyId = key.Id
            ExportId = exportId
            InstallationId = witness.Identity.InstallationId
            Epoch = witness.Identity.Epoch
            CaseId = retained.CaseId
            PreparerActorId = retained.PreparerActorId
            PreparerGrantRevision = retained.PreparerGrantRevision
            ImporterActorId = retained.ImporterActorId
            ExporterActorId = context.Binding.ActorId
            ExporterGrantRevision = context.Binding.GrantRevision
            OperationId = retained.OperationId
            IssuedAt = instant
            ExpiresAt = instant + key.Lifetime
            CanonicalCommandFormat = RecordVersions.CanonicalCommandFormat
            RequestFingerprintVersion = RecordVersions.RequestFingerprint
            Nonce = nonce
            CanonicalRequest = retained.CanonicalRequest
        }

    let private evidence exportId envelope (context: ActorCallContext) bytes used maximum =
        {
            ExportId = exportId
            Artifact = envelope
            ExporterActorId = context.Binding.ActorId
            ExporterGrantRevision = context.Binding.GrantRevision
            ArtifactBytes = bytes
            KeyIssuanceOrdinal = used + 1
            KeyMaximumExports = maximum
        }

    let private commitPrimary
        connection
        transaction
        witness
        (evidence: RecoveryArtifactExportEvidence)
        canonical
        intent
        ct
        =
        task {
            do!
                RecoveryArtifactExportWrite.insert
                    connection
                    transaction
                    evidence
                    canonical
                    intent
                    ct

            do!
                ManagedCopyExports.register
                    connection
                    transaction
                    witness
                    evidence.ExportId
                    evidence.Artifact.CaseId
                    evidence.Artifact.KeyId
                    evidence.Artifact.IssuedAt
                    evidence.Artifact.ExpiresAt
                    (SHA256.HashData evidence.ArtifactBytes)
                    evidence.ArtifactBytes.Length
                    canonical
                    intent
                    ct

            do! transaction.CommitAsync(CancellationToken.None)
        }

    let commit
        connection
        transaction
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (retained: RetainedPreparation)
        (key: RecoveryArtifactIssueKey)
        exportId
        instant
        used
        (encrypt: RecoveryArtifactV3 -> byte array)
        (uncertainStarted: bool ref)
        ct
        =
        task {
            let! nonce = uniqueNonce connection transaction key.Id 8 ct
            let envelope = artifact witness context retained key exportId instant nonce
            let bytes = encrypt envelope

            let evidence = evidence exportId envelope context bytes used key.MaximumExports

            let canonical = RecoveryArtifactExportCandidate.encode evidence

            try
                uncertainStarted.Value <- true
                let intent = witness.BeginAuthority(exportId, canonical, Some retained.CaseId)

                do! commitPrimary connection transaction witness evidence canonical intent ct

                witness.ReconcileAuthority(
                    exportId,
                    intent.Ticket.Sequence,
                    intent.Ticket.Epoch,
                    intent.Ticket.EntryHash,
                    canonical
                )

                return bytes
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }
