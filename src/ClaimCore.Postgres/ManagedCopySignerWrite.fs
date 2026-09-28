namespace ClaimCore.Postgres

open System
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

type internal SignerState =
    {
        PublicKey: byte array
        PublicKeySha256: byte array
        Active: bool
        Revision: int64
        EventHash: byte array
        Purpose: CopySignerPurpose
        HolderActorId: Guid
    }

type internal SignerEventEvidence =
    {
        SigningKeyId: Guid
        Action: string
        OwnerApprovalId: Guid
        CustodianApprovalId: Guid
        Revision: int64
        CandidateSha256: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessEntryHash: byte array
        Purpose: CopySignerPurpose
        HolderActorId: Guid
    }

/// The owner connection alone writes the signer roster. The app role cannot do so.
module internal ManagedCopySignerWrite =
    let state (connection: NpgsqlConnection) transaction keyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT ed25519_public_key,public_key_sha256,active,revision,event_hash,signer_purpose,holder_actor_id "
                    + "FROM claimcore.managed_copy_signers WHERE signing_key_id=@key FOR UPDATE",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            let! rows = command.ExecuteReaderAsync()
            use reader = rows
            let! found = reader.ReadAsync()

            return
                if found then
                    Some
                        {
                            PublicKey = reader.GetFieldValue<byte array>(0)
                            PublicKeySha256 = reader.GetFieldValue<byte array>(1)
                            Active = reader.GetBoolean(2)
                            Revision = reader.GetInt64(3)
                            EventHash = reader.GetFieldValue<byte array>(4)
                            Purpose = ManagedCopySignerCandidate.purposeOfName (reader.GetString(5))
                            HolderActorId = reader.GetGuid(6)
                        }
                else
                    None
        }

    let event (connection: NpgsqlConnection) transaction eventId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT signing_key_id,action_name,owner_approval_id,custodian_approval_id,"
                    + "revision,candidate_sha256,witness_sequence,witness_epoch,witness_entry_hash,"
                    + "signer_purpose,holder_actor_id "
                    + "FROM claimcore.managed_copy_signer_events WHERE event_id=@event",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            let! rows = command.ExecuteReaderAsync()
            use reader = rows
            let! found = reader.ReadAsync()

            return
                if found then
                    Some
                        {
                            SigningKeyId = reader.GetGuid(0)
                            Action = reader.GetString(1)
                            OwnerApprovalId = reader.GetGuid(2)
                            CustodianApprovalId = reader.GetGuid(3)
                            Revision = reader.GetInt64(4)
                            CandidateSha256 = reader.GetFieldValue<byte array>(5)
                            WitnessSequence = reader.GetInt64(6)
                            WitnessEpoch = reader.GetInt64(7)
                            WitnessEntryHash = reader.GetFieldValue<byte array>(8)
                            Purpose = ManagedCopySignerCandidate.purposeOfName (reader.GetString(9))
                            HolderActorId = reader.GetGuid(10)
                        }
                else
                    None
        }

    let private one (command: NpgsqlCommand) =
        task {
            let! count = command.ExecuteNonQueryAsync()

            if count <> 1 then
                invalidOp "Signer roster write was incomplete."
        }

    let register
        connection
        transaction
        keyId
        purpose
        holderActorId
        (publicKey: byte array)
        publicHash
        eventHash
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_signers "
                    + "(signing_key_id,signer_purpose,holder_actor_id,ed25519_public_key,public_key_sha256,active,revision,event_hash) "
                    + "VALUES (@key,@purpose,@holder,@public,@hash,true,1,@eventHash)",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            Sql.text command "purpose" (ManagedCopySignerCandidate.purposeName purpose)
            Sql.uuid command "holder" holderActorId
            Sql.add command "public" NpgsqlDbType.Bytea (box publicKey)
            Sql.add command "hash" NpgsqlDbType.Bytea (box publicHash)
            Sql.add command "eventHash" NpgsqlDbType.Bytea (box eventHash)
            do! one command
        }

    let retire connection transaction keyId priorRevision eventHash =
        task {
            use command =
                new NpgsqlCommand(
                    "UPDATE claimcore.managed_copy_signers SET active=false,revision=@next,"
                    + "event_hash=@eventHash WHERE signing_key_id=@key "
                    + "AND active AND revision=@prior",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            Sql.integer command "next" (priorRevision + 1L)
            Sql.integer command "prior" priorRevision
            Sql.add command "eventHash" NpgsqlDbType.Bytea (box eventHash)
            do! one command
        }

    let append
        connection
        transaction
        eventId
        keyId
        revision
        action
        purpose
        (owner: SignerApprovalEvidence)
        (custodian: SignerApprovalEvidence)
        (canonical: byte array)
        (previous: byte array)
        (eventHash: byte array)
        (intent: WitnessIntent)
        =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_signer_events "
                    + "(event_id,signing_key_id,revision,action_name,signer_purpose,holder_actor_id,owner_actor_id,"
                    + "custodian_actor_id,owner_approval_id,custodian_approval_id,"
                    + "canonical_action,candidate_sha256,previous_hash,event_hash,"
                    + "witness_sequence,witness_epoch,witness_entry_hash) "
                    + "VALUES (@event,@key,@revision,@action,@purpose,@holder,@owner,@custodian,"
                    + "@ownerApproval,@custodianApproval,@canonical,@digest,@previous,"
                    + "@eventHash,@sequence,@epoch,@entryHash)",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            Sql.uuid command "key" keyId
            Sql.integer command "revision" revision
            Sql.text command "action" action
            Sql.text command "purpose" (ManagedCopySignerCandidate.purposeName purpose)
            Sql.uuid command "holder" custodian.ActorId
            Sql.uuid command "owner" owner.ActorId
            Sql.uuid command "custodian" custodian.ActorId
            Sql.uuid command "ownerApproval" owner.ApprovalId
            Sql.uuid command "custodianApproval" custodian.ApprovalId
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "digest" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.add command "previous" NpgsqlDbType.Bytea (box previous)
            Sql.add command "eventHash" NpgsqlDbType.Bytea (box eventHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            do! one command
        }

    let useApproval connection transaction eventId approvalId role =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_signer_approval_uses "
                    + "(approval_id,signer_event_id,actor_role) VALUES (@approval,@event,@role)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            Sql.uuid command "event" eventId
            Sql.text command "role" role
            do! one command
        }
