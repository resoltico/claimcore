namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open DataAuditCommon

[<NoEquality; NoComparison>]
type internal AcceptedAuditRow =
    {
        CaseId: Guid
        RuleRevision: int16
        Receipt: Receipt
        Request: CommandRequest
        CanonicalRequest: byte array
        Snapshot: byte array
        BusinessDate: DateOnly
        ObservedInstant: DateTimeOffset
        WitnessSequence: int64
        WitnessEpoch: int64
        WitnessEntryHash: byte array
        ActorEvidence: AcceptedActorEvidence
    }

/// At most one SQL history window is materialized, then its reader closes before disposition
/// pages are fetched on the same repeatable-read transaction.
module internal DataAuditAcceptedRows =
    let private optionalGuid (reader: NpgsqlDataReader) name =
        let index = reader.GetOrdinal(name)

        if reader.IsDBNull(index) then
            None
        else
            Some(reader.GetGuid(index))

    let private businessDate (reader: NpgsqlDataReader) =
        try
            DateOnly(2000, 1, 1)
                .AddDays(reader.GetInt32(reader.GetOrdinal("effective_business_date")))
        with :? ArgumentOutOfRangeException ->
            corrupt ()

    let private row (reader: NpgsqlDataReader) =
        let ordinal name = reader.GetOrdinal(name)

        let bytes name =
            reader.GetFieldValue<byte array>(ordinal name)

        let canonical = bytes "canonical_request"

        let request =
            match RequestRecord.decode 65536 canonical with
            | Ok value when RequestRecord.encode value = canonical -> value
            | _ -> corrupt ()

        let receipt = Rows.receipt reader false
        let caseId = reader.GetGuid(ordinal "case_id")

        let actors: AcceptedActorEvidence =
            {
                CaseId = caseId
                PreparerActorId = reader.GetGuid(ordinal "preparer_actor_id")
                ImporterActorId = optionalGuid reader "importer_actor_id"
                SubmitterActorId = optionalGuid reader "submitter_actor_id"
                ResolverActorId = optionalGuid reader "resolver_actor_id"
                AcceptedActorId = reader.GetGuid(ordinal "accepted_actor_id")
                GrantRevision = reader.GetInt64(ordinal "grant_revision")
            }

        {
            CaseId = caseId
            RuleRevision = reader.GetInt16(ordinal "rule_revision")
            Receipt = receipt
            Request = request
            CanonicalRequest = canonical
            Snapshot = bytes "snapshot"
            BusinessDate = businessDate reader
            ObservedInstant = reader.GetFieldValue<DateTimeOffset>(ordinal "observed_utc_instant")
            WitnessSequence = reader.GetInt64(ordinal "witness_sequence")
            WitnessEpoch = reader.GetInt64(ordinal "witness_epoch")
            WitnessEntryHash = bytes "witness_entry_hash"
            ActorEvidence = actors
        }

    let readPage
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        reference
        after
        (cancellationToken: CancellationToken)
        =
        task {
            use command = new NpgsqlCommand(Sql.history, connection, transaction)
            Sql.text command "reference" reference
            Sql.integer command "after" after
            let! result = command.ExecuteReaderAsync(cancellationToken)
            use reader = result
            let rows = ResizeArray<AcceptedAuditRow>()
            let mutable reading = true

            while reading do
                let! found = reader.ReadAsync(cancellationToken)
                reading <- found

                if found then
                    rows.Add(row reader)

            return List.ofSeq rows
        }
