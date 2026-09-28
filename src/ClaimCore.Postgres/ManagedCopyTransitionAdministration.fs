namespace ClaimCore.Postgres

open System
open System.Data
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open WitnessProtocolReconciliation
open ClaimCore.Application

/// Owner-only custody intent and uncertainty transitions. Verified states require separate proof.
module internal ManagedCopyTransitionAdministration =
    let held (connection: NpgsqlConnection) transaction (sourceCaseId: Guid option) =
        task {
            let predicate = if sourceCaseId.IsSome then "case_id=@case" else "TRUE"

            use command =
                new NpgsqlCommand(
                    "SELECT EXISTS(SELECT 1 FROM claimcore.case_holds WHERE released_at IS NULL AND "
                    + predicate
                    + ") OR EXISTS(SELECT 1 FROM claimcore.case_erasure_holds h "
                    + "LEFT JOIN claimcore.case_erasure_hold_releases r ON r.hold_id=h.hold_id "
                    + "WHERE r.hold_id IS NULL AND "
                    + (if sourceCaseId.IsSome then "h.case_id=@case" else "TRUE")
                    + ")",
                    connection,
                    transaction
                )

            sourceCaseId |> Option.iter (Sql.uuid command "case")
            let! value = command.ExecuteScalarAsync()
            return unbox<bool> value
        }

    let private updateProjection
        connection
        transaction
        (transition: ManagedCopyTransition)
        eventHash
        =
        task {
            use projection =
                new NpgsqlCommand(
                    "UPDATE claimcore.managed_copies SET state=@state,revision=@revision,"
                    + "event_hash=@eventHash WHERE copy_id=@copy AND revision=@previous",
                    connection,
                    transaction
                )

            Sql.text projection "state" transition.State
            Sql.integer projection "revision" transition.Revision
            Sql.add projection "eventHash" NpgsqlDbType.Bytea (box eventHash)
            Sql.uuid projection "copy" transition.Copy.CopyId
            Sql.integer projection "previous" (transition.Revision - 1L)
            let! updated = projection.ExecuteNonQueryAsync()

            if updated <> 1 then
                invalidOp "Managed-copy projection update was incomplete."
        }

    let insertEvent
        connection
        transaction
        (transition: ManagedCopyTransition)
        canonical
        signature
        eventHash
        (intent: WitnessIntent)
        =
        task {
            use entry =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_events "
                    + "(event_id,copy_id,revision,event_kind,producer_kind,canonical_attestation,"
                    + "signing_key_id,ed25519_signature,candidate_sha256,previous_hash,event_hash,"
                    + "witness_sequence,witness_epoch,witness_entry_hash) VALUES "
                    + "(@event,@copy,@revision,@kind,'OWNER_ATTESTED',@canonical,@key,@signature,"
                    + "@candidate,@previous,@eventHash,@sequence,@epoch,@entryHash)",
                    connection,
                    transaction
                )

            Sql.uuid entry "event" transition.Copy.EventId
            Sql.uuid entry "copy" transition.Copy.CopyId
            Sql.integer entry "revision" transition.Revision
            Sql.text entry "kind" transition.EventKind
            Sql.add entry "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.uuid entry "key" transition.Copy.SigningKeyId
            Sql.add entry "signature" NpgsqlDbType.Bytea (box signature)
            Sql.add entry "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.add entry "previous" NpgsqlDbType.Bytea (box transition.PreviousEventHash)
            Sql.add entry "eventHash" NpgsqlDbType.Bytea (box eventHash)
            Sql.integer entry "sequence" intent.Ticket.Sequence
            Sql.integer entry "epoch" intent.Ticket.Epoch
            Sql.add entry "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! inserted = entry.ExecuteNonQueryAsync()

            if inserted <> 1 then
                invalidOp "Managed-copy transition insert was incomplete."
        }

    let private write
        connection
        transaction
        (transition: ManagedCopyTransition)
        canonical
        signature
        intent
        =
        task {
            let eventHash =
                ManagedCopyEventHash.compute transition.PreviousEventHash canonical (Some signature)

            do! updateProjection connection transaction transition eventHash
            do! insertEvent connection transaction transition canonical signature eventHash intent
        }

    let exactRetry
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        canonical
        signature
        (stored: ManagedCopyEventEvidence)
        =
        let expected = ManagedCopyTransitionPolicy.candidate transition canonical signature

        if
            stored.CopyId <> transition.Copy.CopyId
            || stored.Canonical <> canonical
            || stored.Signature <> signature
            || stored.CandidateSha256 <> SHA256.HashData(expected)
        then
            AuthorityWriteOutcome.Refused
        else
            witness.VerifyAuthorityEvidence(
                transition.Copy.EventId,
                stored.WitnessSequence,
                stored.WitnessEpoch,
                stored.WitnessEntryHash,
                stored.CandidateSha256
            )

            AuthorityWriteOutcome.Applied(transition.Copy.EventId, transition.Revision)

    let private permitted
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        (canonical: byte array)
        (signature: byte array)
        (state: ManagedCopyCurrent)
        (publicKey: byte array)
        (publicDigest: byte array)
        heldCopy
        =
        let original = ManagedCopyRegistrationAttestation.parse state.Registration

        original
        |> Option.exists (fun value -> ManagedCopyTransitionPolicy.sameCopy value transition.Copy)
        && transition.Revision = state.Revision + 1L
        && transition.PreviousEventHash = state.EventHash
        && transition.Copy.InstallationId = witness.Identity.InstallationId
        && transition.Copy.LineageId = witness.Identity.LineageId
        && transition.Copy.Epoch = witness.Identity.Epoch
        && publicDigest = SHA256.HashData(publicKey)
        && ManagedCopySignature.verify publicKey canonical signature
        && ManagedCopyTransitionPolicy.allowed state transition DateTimeOffset.UtcNow heldCopy

    let private commit
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        canonical
        signature
        =
        task {
            let exact = ManagedCopyTransitionPolicy.candidate transition canonical signature

            try
                let intent =
                    witness.BeginAuthority(
                        transition.Copy.EventId,
                        exact,
                        transition.Copy.SourceCaseId
                    )

                do! write connection transaction transition canonical signature intent
                do! transaction.CommitAsync()
                witness.SettleAuthority(transition.Copy.EventId, intent) |> ignore
                return AuthorityWriteOutcome.Applied(transition.Copy.EventId, transition.Revision)
            finally
                CryptographicOperations.ZeroMemory(exact)
        }

    let private newEvent
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (transition: ManagedCopyTransition)
        canonical
        signature
        =
        task {
            let! current =
                ManagedCopyOwnerRead.current connection transaction transition.Copy.CopyId

            let! signer =
                ManagedCopyOwnerRead.signer connection transaction transition.Copy.SigningKeyId

            let! heldCopy = held connection transaction transition.Copy.SourceCaseId

            let historical =
                try
                    witness.VerifyHistoricalTip(
                        transition.ActionWitnessCutoffSequence,
                        transition.ActionWitnessCutoffHash
                    )

                    true
                with _ ->
                    false

            match current, signer with
            | Some state, Some(publicKey, publicDigest, true, CopySignerPurpose.CopyAttestor) when
                historical
                && transition.ActionWitnessCutoffSequence >= transition.Copy.WitnessCutoffSequence
                && permitted
                    witness
                    transition
                    canonical
                    signature
                    state
                    publicKey
                    publicDigest
                    heldCopy
                ->
                return! commit connection transaction witness transition canonical signature
            | _ -> return AuthorityWriteOutcome.Refused
        }

    let transition
        (connection: NpgsqlConnection)
        (witness: WitnessProtocol)
        (canonical: byte array)
        (signature: byte array)
        =
        task {
            match ManagedCopyTransitionAttestation.parse canonical with
            | None -> return AuthorityWriteOutcome.Refused
            | Some _ when isNull (box signature) || signature.Length <> 64 ->
                return AuthorityWriteOutcome.Refused
            | Some transition ->
                try
                    OwnerConnection.requireIdentity connection
                    SchemaBaseline.requireCurrent connection
                    witness.Admit()
                    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

                    let! _ =
                        ActorGrantRead.lockRevision
                            connection
                            transaction
                            true
                            Threading.CancellationToken.None

                    let! existing =
                        ManagedCopyOwnerRead.transitionEvent
                            connection
                            transaction
                            transition.Copy.EventId

                    match existing with
                    | Some stored -> return exactRetry witness transition canonical signature stored
                    | None ->
                        return!
                            newEvent connection transaction witness transition canonical signature
                with _ ->
                    return AuthorityWriteOutcome.Unconfirmed transition.Copy.EventId
        }
