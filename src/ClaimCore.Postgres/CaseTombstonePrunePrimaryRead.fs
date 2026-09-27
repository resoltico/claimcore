namespace ClaimCore.Postgres

open System
open System.IO
open Npgsql

[<NoEquality; NoComparison>]
type internal StoredWitnessPruneReceipt =
    {
        EventId: Guid
        CaseId: Guid
        Canonical: byte array
        CandidateHash: byte array
        IntentSequence: int64
        IntentEpoch: int64
        IntentHash: byte array
        CutoffSequence: int64
        CutoffHash: byte array
        TargetCount: int64
        TargetDigest: byte array
        CopyInventoryDigest: byte array
        AuthorityRevision: int64
        AuthorityHash: byte array
        ValidUntil: DateTimeOffset
        PurgeEventId: Guid
        PurgeWitnessSequence: int64
        PurgeWitnessEpoch: int64
        PurgeWitnessHash: byte array
    }

module internal CaseTombstonePrunePrimaryRead =
    let private sql =
        "SELECT witness_prune_event_id,witness_prune_canonical_action,"
        + "witness_prune_candidate_sha256,witness_prune_intent_sequence,"
        + "witness_prune_intent_epoch,witness_prune_intent_hash,"
        + "witness_prune_cutoff_sequence,witness_prune_cutoff_hash,"
        + "witness_prune_target_count,witness_prune_target_digest,"
        + "witness_prune_copy_inventory_sha256,witness_prune_authority_revision,"
        + "witness_prune_authority_hash,witness_prune_valid_until,"
        + "purge_event_id,purge_witness_sequence,purge_witness_epoch,purge_witness_entry_hash "
        + "FROM claimcore.case_erasure_tombstones WHERE case_id=@case "
        + "AND phase IN ('ERASURE_PENDING','PAYLOAD_ERASED_SUPPRESSION_RETAINED',"
        + "'ERASURE_FINAL') AND witness_prune_event_id IS NOT NULL"

    let find (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) caseId =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let value =
                    {
                        EventId = reader.GetGuid(0)
                        CaseId = caseId
                        Canonical = reader.GetFieldValue<byte array>(1)
                        CandidateHash = reader.GetFieldValue<byte array>(2)
                        IntentSequence = reader.GetInt64(3)
                        IntentEpoch = reader.GetInt64(4)
                        IntentHash = reader.GetFieldValue<byte array>(5)
                        CutoffSequence = reader.GetInt64(6)
                        CutoffHash = reader.GetFieldValue<byte array>(7)
                        TargetCount = reader.GetInt64(8)
                        TargetDigest = reader.GetFieldValue<byte array>(9)
                        CopyInventoryDigest = reader.GetFieldValue<byte array>(10)
                        AuthorityRevision = reader.GetInt64(11)
                        AuthorityHash = reader.GetFieldValue<byte array>(12)
                        ValidUntil = reader.GetFieldValue<DateTimeOffset>(13)
                        PurgeEventId = reader.GetGuid(14)
                        PurgeWitnessSequence = reader.GetInt64(15)
                        PurgeWitnessEpoch = reader.GetInt64(16)
                        PurgeWitnessHash = reader.GetFieldValue<byte array>(17)
                    }

                if reader.Read() then
                    raise (InvalidDataException("Witness prune receipt is duplicated."))

                return Some value
        }
