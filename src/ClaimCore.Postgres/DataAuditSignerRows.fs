namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application

type internal SignerProjectionAuditRow =
    {
        KeyId: Guid
        PublicKey: byte array
        PublicHash: byte array
        Active: bool
        Revision: int64
        EventHash: byte array
        Purpose: CopySignerPurpose
        HolderActorId: Guid
    }

type internal SignerEventAuditRow =
    {
        EventId: Guid
        Revision: int64
        Action: string
        OwnerActorId: Guid
        CustodianActorId: Guid
        OwnerApprovalId: Guid
        CustodianApprovalId: Guid
        Canonical: byte array
        CandidateHash: byte array
        PreviousHash: byte array
        EventHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
        Purpose: CopySignerPurpose
        HolderActorId: Guid
    }

module internal DataAuditSignerRows =
    let projectionPage connection transaction after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT signing_key_id,ed25519_public_key,public_key_sha256,active,"
                    + "revision,event_hash,signer_purpose,holder_actor_id FROM claimcore.managed_copy_signers "
                    + "WHERE signing_key_id>@after ORDER BY signing_key_id LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<SignerProjectionAuditRow>()

            while reader.Read() do
                rows.Add
                    {
                        KeyId = reader.GetGuid(0)
                        PublicKey = reader.GetFieldValue<byte array>(1)
                        PublicHash = reader.GetFieldValue<byte array>(2)
                        Active = reader.GetBoolean(3)
                        Revision = reader.GetInt64(4)
                        EventHash = reader.GetFieldValue<byte array>(5)
                        Purpose = ManagedCopySignerCandidate.purposeOfName (reader.GetString(6))
                        HolderActorId = reader.GetGuid(7)
                    }

            return rows |> Seq.toList
        }

    let events connection transaction keyId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT event_id,revision,action_name,owner_actor_id,custodian_actor_id,"
                    + "owner_approval_id,custodian_approval_id,canonical_action,candidate_sha256,"
                    + "previous_hash,event_hash,witness_sequence,witness_epoch,witness_entry_hash,"
                    + "signer_purpose,holder_actor_id "
                    + "FROM claimcore.managed_copy_signer_events WHERE signing_key_id=@key "
                    + "ORDER BY revision LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "key" keyId
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<SignerEventAuditRow>()

            while reader.Read() do
                rows.Add
                    {
                        EventId = reader.GetGuid(0)
                        Revision = reader.GetInt64(1)
                        Action = reader.GetString(2)
                        OwnerActorId = reader.GetGuid(3)
                        CustodianActorId = reader.GetGuid(4)
                        OwnerApprovalId = reader.GetGuid(5)
                        CustodianApprovalId = reader.GetGuid(6)
                        Canonical = reader.GetFieldValue<byte array>(7)
                        CandidateHash = reader.GetFieldValue<byte array>(8)
                        PreviousHash = reader.GetFieldValue<byte array>(9)
                        EventHash = reader.GetFieldValue<byte array>(10)
                        WitnessSequence = reader.GetInt64(11)
                        WitnessEpoch = reader.GetInt64(12)
                        WitnessHash = reader.GetFieldValue<byte array>(13)
                        Purpose = ManagedCopySignerCandidate.purposeOfName (reader.GetString(14))
                        HolderActorId = reader.GetGuid(15)
                    }

            return rows |> Seq.toList
        }

    let approvalUses connection transaction eventId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT approval_id,actor_role FROM claimcore.managed_copy_signer_approval_uses "
                    + "WHERE signer_event_id=@event ORDER BY actor_role LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "event" eventId
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<string * Guid>()

            while reader.Read() do
                rows.Add(reader.GetString(1), reader.GetGuid(0))

            return rows |> Seq.toList
        }
