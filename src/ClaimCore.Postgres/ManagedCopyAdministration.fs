namespace ClaimCore.Postgres

open WitnessProtocolReconciliation

open System
open System.Buffers.Binary
open System.Data
open System.Security.Cryptography
open System.Text
open System.Threading
open Npgsql
open ClaimCore.Application

/// Owner-only signed copy ingest. REGISTER records custody; it does not verify a restore.
module internal ManagedCopyAdministration =
    let internal candidate (value: ManagedCopyAttestation) (canonical: byte array) signature =
        let length = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteInt32BigEndian(length, canonical.Length)

        Array.concat
            [
                Encoding.ASCII.GetBytes("CLAIMCORE_OWNER_MANAGED_COPY_REGISTER_V1\000")
                value.EventId.ToByteArray()
                value.SigningKeyId.ToByteArray()
                length
                canonical
                signature
            ]

    let private identity
        connection
        transaction
        (witness: WitnessProtocol)
        (value: ManagedCopyAttestation)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT installation_id,lineage_id,witness_epoch "
                    + "FROM claimcore.installation_lineage WHERE singleton",
                    connection,
                    transaction
                )

            let! rows = command.ExecuteReaderAsync()
            use reader = rows
            let! found = reader.ReadAsync()

            return
                found
                && reader.GetGuid(0) = value.InstallationId
                && reader.GetGuid(1) = value.LineageId
                && reader.GetInt64(2) = value.Epoch
                && value.InstallationId = witness.Identity.InstallationId
                && value.LineageId = witness.Identity.LineageId
                && value.Epoch = witness.Identity.Epoch
                && not (reader.Read())
        }

    let private caseRegistrationBlocked connection transaction =
        function
        | None -> Threading.Tasks.Task.FromResult false
        | Some caseId ->
            task {
                use command =
                    new NpgsqlCommand(
                        "SELECT EXISTS(SELECT 1 FROM claimcore.case_erasure_tombstones "
                        + "WHERE case_id=@case) OR EXISTS(SELECT 1 FROM claimcore.cases "
                        + "WHERE case_id=@case AND privacy_phase<>'ACTIVE')",
                        connection,
                        transaction
                    )

                Sql.uuid command "case" caseId
                let! value = command.ExecuteScalarAsync()
                return value :?> bool
            }

    let private replay
        (witness: WitnessProtocol)
        (value: ManagedCopyAttestation)
        canonical
        signature
        (stored: ManagedCopyEventEvidence)
        =
        task {
            if
                stored.CopyId <> value.CopyId
                || stored.Canonical <> canonical
                || stored.Signature <> signature
            then
                return AuthorityWriteOutcome.Refused
            else
                do!
                    witness.VerifyAuthorityEvidence(
                        value.EventId,
                        stored.WitnessSequence,
                        stored.WitnessEpoch,
                        stored.WitnessEntryHash,
                        stored.CandidateSha256,
                        CancellationToken.None
                    )

                return AuthorityWriteOutcome.Applied(value.EventId, 1L)
        }

    let private persistRegistration
        connection
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (value: ManagedCopyAttestation)
        canonical
        signature
        =
        task {
            let exact = candidate value canonical signature

            try
                let eventHash =
                    ManagedCopyEventHash.compute
                        (Array.zeroCreate<byte> 32)
                        canonical
                        (Some signature)

                let! intent =
                    witness.BeginAuthority(
                        value.EventId,
                        exact,
                        value.SourceCaseId,
                        CancellationToken.None
                    )

                do! ManagedCopyOwnerWrite.insertCopy connection transaction value eventHash

                do!
                    ManagedCopyOwnerWrite.insertEvent
                        connection
                        transaction
                        value
                        canonical
                        signature
                        (Array.zeroCreate<byte> 32)
                        eventHash
                        intent

                do! transaction.CommitAsync()
                let! _ = witness.SettleAuthority(value.EventId, intent)
                return AuthorityWriteOutcome.Applied(value.EventId, 1L)
            finally
                CryptographicOperations.ZeroMemory(exact)
        }

    let private register
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (value: ManagedCopyAttestation)
        (canonical: byte array)
        (signature: byte array)
        =
        task {
            let! present = ManagedCopyOwnerRead.copyExists connection transaction value.CopyId
            let! signer = ManagedCopyOwnerRead.signer connection transaction value.SigningKeyId

            let! instant = Sql.databaseNow connection transaction CancellationToken.None

            let! blocked = caseRegistrationBlocked connection transaction value.SourceCaseId

            match signer with
            | Some(publicKey, publicDigest, true, CopySignerPurpose.CopyAttestor) when
                not present
                && not blocked
                && publicDigest = SHA256.HashData(publicKey)
                && ManagedCopySignature.verify publicKey canonical signature
                && value.CapturedAt <= instant.AddMinutes(5.0)
                && value.RetainUntil > instant
                ->
                return! persistRegistration connection transaction witness value canonical signature
            | _ -> return AuthorityWriteOutcome.Refused
        }

    let ingest
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (canonical: byte array)
        (signature: byte array)
        =
        task {
            match ManagedCopyRegistrationAttestation.parse canonical with
            | None -> return AuthorityWriteOutcome.Refused
            | Some _ when isNull (box signature) || signature.Length <> 64 ->
                return AuthorityWriteOutcome.Refused
            | Some value ->
                try
                    OwnerConnection.requireIdentity connection
                    SchemaBaseline.requireCurrent connection
                    do! witness.Admit(CancellationToken.None)

                    use! _authorityFence =
                        AuthorityOperationFence.acquireShared None connection CancellationToken.None

                    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

                    let! _ =
                        ActorGrantRead.lockRevision
                            connection
                            transaction
                            true
                            CancellationToken.None

                    let! sameInstallation = identity connection transaction witness value

                    if not sameInstallation then
                        return AuthorityWriteOutcome.Refused
                    else
                        do!
                            witness.VerifyHistoricalTip(
                                value.WitnessCutoffSequence,
                                value.WitnessCutoffHash,
                                CancellationToken.None
                            )

                        let! prior =
                            ManagedCopyOwnerRead.existingEvent connection transaction value.EventId

                        match prior with
                        | Some stored -> return! replay witness value canonical signature stored
                        | None ->
                            return!
                                register connection transaction witness value canonical signature
                with _ ->
                    return AuthorityWriteOutcome.Unconfirmed value.EventId
        }
