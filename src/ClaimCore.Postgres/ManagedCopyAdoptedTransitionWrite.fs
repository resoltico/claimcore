namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

/// Co-commits one adopted-copy projection, signed event, and optional one-use approval.
module internal ManagedCopyAdoptedTransitionWrite =
    let private updateProjection connection transaction (value: AdoptedCopyTransition) hash =
        task {
            use projection =
                new NpgsqlCommand(
                    "UPDATE claimcore.managed_copies SET state=@state,revision=@revision,event_hash=@hash,"
                    + "verification_proof_sha256=@verification,last_verified_at=@verified,"
                    + "deletion_proof_sha256=@deletion WHERE copy_id=@copy AND revision=@previous",
                    connection,
                    transaction
                )

            Sql.text projection "state" value.State
            Sql.integer projection "revision" value.Revision
            Sql.add projection "hash" NpgsqlDbType.Bytea (box hash)
            Sql.optional projection "verification" NpgsqlDbType.Bytea value.VerificationProofSha256
            Sql.optional projection "verified" NpgsqlDbType.TimestampTz value.LastVerifiedAt
            Sql.optional projection "deletion" NpgsqlDbType.Bytea value.DeletionProofSha256
            Sql.uuid projection "copy" value.CopyId
            Sql.integer projection "previous" (value.Revision - 1L)
            let! updated = projection.ExecuteNonQueryAsync()

            if updated <> 1 then
                invalidOp "Adopted-copy projection update was incomplete."
        }

    let private insertEvent
        connection
        transaction
        signingKeyId
        (value: AdoptedCopyTransition)
        canonical
        signature
        hash
        (intent: WitnessIntent)
        =
        task {
            use entry =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_events "
                    + "(event_id,copy_id,revision,event_kind,producer_kind,canonical_attestation,"
                    + "signing_key_id,ed25519_signature,candidate_sha256,previous_hash,event_hash,"
                    + "witness_sequence,witness_epoch,witness_entry_hash) VALUES "
                    + "(@event,@copy,@revision,@kind,@producer,@canonical,@key,@signature,"
                    + "@candidate,@previous,@hash,@sequence,@epoch,@entryHash)",
                    connection,
                    transaction
                )

            Sql.uuid entry "event" value.EventId
            Sql.uuid entry "copy" value.CopyId
            Sql.integer entry "revision" value.Revision
            Sql.text entry "kind" value.EventKind
            Sql.text entry "producer" value.ProducerKind
            Sql.add entry "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.uuid entry "key" signingKeyId
            Sql.add entry "signature" NpgsqlDbType.Bytea (box signature)
            Sql.add entry "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.add entry "previous" NpgsqlDbType.Bytea (box value.PreviousEventHash)
            Sql.add entry "hash" NpgsqlDbType.Bytea (box hash)
            Sql.integer entry "sequence" intent.Ticket.Sequence
            Sql.integer entry "epoch" intent.Ticket.Epoch
            Sql.add entry "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            let! inserted = entry.ExecuteNonQueryAsync()

            if inserted <> 1 then
                invalidOp "Adopted-copy event insert was incomplete."
        }

    let private consumeApproval connection transaction (value: AdoptedCopyTransition) =
        task {
            match value.DeletionApprovalId with
            | None -> ()
            | Some approvalId ->
                use consumption =
                    new NpgsqlCommand(
                        "INSERT INTO claimcore.managed_copy_deletion_approval_uses "
                        + "(approval_id,deletion_event_id) VALUES (@approval,@event)",
                        connection,
                        transaction
                    )

                Sql.uuid consumption "approval" approvalId
                Sql.uuid consumption "event" value.EventId
                let! count = consumption.ExecuteNonQueryAsync()

                if count <> 1 then
                    invalidOp "Adopted-copy approval was not consumed."
        }

    let private write
        connection
        transaction
        signingKeyId
        (value: AdoptedCopyTransition)
        canonical
        signature
        (intent: WitnessIntent)
        =
        task {
            let hash =
                ManagedCopyEventHash.compute value.PreviousEventHash canonical (Some signature)

            do! updateProjection connection transaction value hash

            do!
                insertEvent
                    connection
                    transaction
                    signingKeyId
                    value
                    canonical
                    signature
                    hash
                    intent

            do! consumeApproval connection transaction value
        }

    let commit
        connection
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (value: AdoptedCopyTransition)
        signingKeyId
        canonical
        signature
        =
        task {
            let candidate =
                ManagedCopyAdoptedTransitionPolicy.candidate value canonical signature

            try
                let! intent =
                    witness.BeginAuthority(
                        value.EventId,
                        candidate,
                        Some value.SourceCaseId,
                        CancellationToken.None
                    )

                do! write connection transaction signingKeyId value canonical signature intent
                do! transaction.CommitAsync()
                let! _ = witness.SettleAuthority(value.EventId, intent)
                return AuthorityWriteOutcome.Applied(value.EventId, value.Revision)
            finally
                CryptographicOperations.ZeroMemory(candidate)
        }
