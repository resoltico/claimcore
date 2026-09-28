namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open DataAuditCommon

[<NoEquality; NoComparison>]
type internal AdoptedCopyEventRow =
    {
        EventId: Guid
        Revision: int64
        EventKind: string
        ProducerKind: string
        Canonical: byte array
        SigningKeyId: Guid
        Signature: byte array
        CandidateDigest: byte array
        PreviousHash: byte array
        EventHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessEntryHash: byte array
        ApprovalUseId: Guid option
    }

/// Bounded immutable-key pages of signed post-adoption copy events.
module internal DataAuditAdoptedCopyRows =
    let page connection transaction copyId after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT e.event_id,e.revision,e.event_kind,e.producer_kind,"
                    + "e.canonical_attestation,e.signing_key_id,e.ed25519_signature,"
                    + "e.candidate_sha256,e.previous_hash,e.event_hash,e.witness_sequence,"
                    + "e.witness_epoch,e.witness_entry_hash,u.approval_id "
                    + "FROM claimcore.managed_copy_events e "
                    + "LEFT JOIN claimcore.managed_copy_deletion_approval_uses u "
                    + "ON u.deletion_event_id=e.event_id "
                    + "WHERE e.copy_id=@copy AND e.revision>@after "
                    + "ORDER BY e.revision LIMIT 100",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" copyId
            Sql.integer command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let page = ResizeArray<AdoptedCopyEventRow>()

            while reader.Read() do
                if reader.IsDBNull(5) || reader.IsDBNull(6) then
                    corrupt ()

                page.Add
                    {
                        EventId = reader.GetGuid(0)
                        Revision = reader.GetInt64(1)
                        EventKind = reader.GetString(2)
                        ProducerKind = reader.GetString(3)
                        Canonical = reader.GetFieldValue<byte array>(4)
                        SigningKeyId = reader.GetGuid(5)
                        Signature = reader.GetFieldValue<byte array>(6)
                        CandidateDigest = reader.GetFieldValue<byte array>(7)
                        PreviousHash = reader.GetFieldValue<byte array>(8)
                        EventHash = reader.GetFieldValue<byte array>(9)
                        WitnessSequence = reader.GetInt64(10)
                        WitnessEpoch = reader.GetInt64(11)
                        WitnessEntryHash = reader.GetFieldValue<byte array>(12)
                        ApprovalUseId =
                            if reader.IsDBNull(13) then
                                None
                            else
                                Some(reader.GetGuid(13))
                    }

            return page.ToArray()
        }
