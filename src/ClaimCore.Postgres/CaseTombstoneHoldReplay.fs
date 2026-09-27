namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open Npgsql
open ClaimCore.Application
open WitnessProtocolReconciliation

[<NoEquality; NoComparison>]
type internal TombstoneHoldReceipt =
    {
        CaseId: Guid
        Revision: int64
        PreviousHash: byte array
        ActorId: Guid
        GrantRevision: int64
        ObservedAt: DateTimeOffset
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessHash: byte array
    }

module internal CaseTombstoneHoldReplay =
    let private sql =
        "SELECT case_id,record_revision,record_previous_hash,recorded_by,"
        + "recorded_grant_revision,recorded_at,record_canonical_action,"
        + "record_candidate_sha256,record_witness_sequence,record_witness_epoch,"
        + "record_witness_hash FROM claimcore.case_erasure_holds WHERE record_event_id=@event "
        + "UNION ALL SELECT case_id,release_revision,release_previous_hash,released_by,"
        + "released_grant_revision,released_at,release_canonical_action,"
        + "release_candidate_sha256,release_witness_sequence,release_witness_epoch,"
        + "release_witness_hash FROM claimcore.case_erasure_hold_releases "
        + "WHERE release_event_id=@event"

    let find (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) eventId =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            Sql.uuid command "event" eventId
            use! reader = command.ExecuteReaderAsync()
            let! found = reader.ReadAsync()

            if not found then
                return None
            else
                let value =
                    {
                        CaseId = reader.GetGuid(0)
                        Revision = reader.GetInt64(1)
                        PreviousHash = reader.GetFieldValue<byte array>(2)
                        ActorId = reader.GetGuid(3)
                        GrantRevision = reader.GetInt64(4)
                        ObservedAt = reader.GetFieldValue<DateTimeOffset>(5)
                        Canonical = reader.GetFieldValue<byte array>(6)
                        CandidateHash = reader.GetFieldValue<byte array>(7)
                        WitnessSequence = reader.GetInt64(8)
                        WitnessEpoch = reader.GetInt64(9)
                        WitnessHash = reader.GetFieldValue<byte array>(10)
                    }

                if reader.Read() then
                    raise (InvalidOperationException("Tombstone hold event ID is duplicated."))

                return Some value
        }

    let reconcile
        (witness: WitnessProtocol)
        (context: ActorCallContext)
        (change: TombstoneHoldChange)
        (receipt: TombstoneHoldReceipt)
        =
        let expectedHash =
            try
                Convert.FromHexString(change.ExpectedAuthorityHash)
            with _ ->
                Array.empty

        let canonical =
            CaseTombstoneCandidate.hold
                change
                receipt.ActorId
                receipt.GrantRevision
                receipt.PreviousHash
                receipt.ObservedAt

        try
            if
                receipt.CaseId <> change.CaseId
                || receipt.ActorId <> context.Binding.ActorId
                || receipt.Revision <> change.ExpectedAuthorityRevision + 1L
                || receipt.PreviousHash <> expectedHash
                || receipt.Canonical <> canonical
                || receipt.CandidateHash <> SHA256.HashData(canonical)
            then
                TombstoneWriteOutcome.Refused ClaimCore.Domain.LifecycleRefusal.VersionConflict
            else
                try
                    witness.ReconcileAuthority(
                        change.EventId,
                        receipt.WitnessSequence,
                        receipt.WitnessEpoch,
                        receipt.WitnessHash,
                        receipt.Canonical
                    )

                    TombstoneWriteOutcome.Applied(change.EventId, receipt.Revision)
                with _ ->
                    TombstoneWriteOutcome.Unconfirmed change.EventId
        finally
            CryptographicOperations.ZeroMemory(canonical)
