namespace ClaimCore.Postgres

open System
open System.Buffers.Binary
open System.IO
open System.Security.Cryptography
open System.Text
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.Witness

exception internal CaseIdentityCoverageUnknowable

type internal WitnessDenialSeal =
    {
        CutoffSequence: int64
        CutoffHash: byte array
        IntentCount: int64
        IntentDigest: byte array
        DenialCount: int64
        DenialDigest: byte array
    }

/// Tentative HMAC denials are written only inside the owner's uncommitted purge transaction.
/// A late chain failure or an unlinked intent must roll the entire transaction back.
module internal CaseErasureWitnessDenials =
    let private sourceSql =
        "SELECT count(*) FROM ("
        + "SELECT operation_id AS id FROM claimcore.case_changes WHERE case_id=@case AND operation_id=@event "
        + "UNION ALL SELECT operation_id FROM claimcore.operation_revocations "
        + "WHERE case_id=@case AND operation_id=@event "
        + "UNION ALL SELECT witness_event_id FROM claimcore.request_preparations "
        + "WHERE case_id=@case AND witness_event_id=@event "
        + "UNION ALL SELECT a.witness_event_id FROM claimcore.request_submission_attempts a "
        + "JOIN claimcore.request_preparations p ON p.operation_id=a.operation_id "
        + "WHERE p.case_id=@case AND a.witness_event_id=@event "
        + "UNION ALL SELECT export_id FROM claimcore.recovery_artifact_exports "
        + "WHERE case_id=@case AND export_id=@event "
        + "UNION ALL SELECT publication_id FROM claimcore.managed_copy_external_publications "
        + "WHERE case_id=@case AND publication_id=@event "
        + "UNION ALL SELECT event_id FROM claimcore.case_lifecycle_events "
        + "WHERE case_id=@case AND event_id=@event "
        + "UNION ALL SELECT approval_id FROM claimcore.case_lifecycle_approvals "
        + "WHERE case_id=@case AND approval_id=@event "
        + "UNION ALL SELECT e.event_id FROM claimcore.managed_copy_events e "
        + "JOIN claimcore.managed_copies c ON c.copy_id=e.copy_id "
        + "WHERE c.source_case_id=@case AND e.event_id=@event "
        + "AND e.producer_kind='OWNER_ATTESTED') sources"

    let private actorSource
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        eventId
        caseId
        =
        use command =
            new NpgsqlCommand(
                "SELECT revision,action_name,target_actor_id,approver_actor_id,canonical_action "
                + "FROM claimcore.actor_authority_events WHERE event_id=@event",
                connection,
                transaction
            )

        Sql.uuid command "event" eventId
        use reader = command.ExecuteReader()

        if not (reader.Read()) then
            false
        else
            let approver = if reader.IsDBNull(3) then None else Some(reader.GetGuid(3))

            let action =
                ActorGrantCandidate.decodeStored
                    (reader.GetFieldValue<byte array>(4))
                    (reader.GetInt64(0))
                    eventId
                    (reader.GetString(1))
                    (reader.GetGuid(2))
                    approver

            if reader.Read() then
                raise CaseIdentityCoverageUnknowable

            match action |> Option.bind _.Grant with
            | Some { Scope = GrantScope.Case bound } when bound = caseId -> true
            | _ -> false

    let private requireSource connection transaction eventId caseId =
        use command = new NpgsqlCommand(sourceSql, connection, transaction)
        Sql.uuid command "case" caseId
        Sql.uuid command "event" eventId
        let regularCount = command.ExecuteScalar() :?> int64

        let actorCount =
            if actorSource connection transaction eventId caseId then
                1L
            else
                0L

        if regularCount + actorCount <> 1L then
            raise CaseIdentityCoverageUnknowable

    let private retain
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        caseId
        (commitments: ISuppressionCommitments)
        (intent: SubjectOperation)
        =
        let digest = commitments.Operation intent.OperationId

        if digest.Length <> 32 then
            invalidOp "Witnessed intent commitment is invalid."

        use command =
            new NpgsqlCommand(
                "INSERT INTO claimcore.case_erasure_operation_denials "
                + "(operation_commitment,case_id,knowledge,witness_intent_sequence,"
                + "witness_intent_epoch,witness_intent_entry_hash) "
                + "VALUES (@digest,@case,'WITNESSED_INTENT',@sequence,@epoch,@hash) "
                + "ON CONFLICT (operation_commitment) DO UPDATE SET "
                + "witness_intent_sequence=EXCLUDED.witness_intent_sequence,"
                + "witness_intent_epoch=EXCLUDED.witness_intent_epoch,"
                + "witness_intent_entry_hash=EXCLUDED.witness_intent_entry_hash "
                + "WHERE claimcore.case_erasure_operation_denials.case_id=EXCLUDED.case_id "
                + "AND claimcore.case_erasure_operation_denials.witness_intent_sequence IS NULL",
                connection,
                transaction
            )

        Sql.add command "digest" NpgsqlDbType.Bytea (box digest)
        Sql.uuid command "case" caseId
        Sql.integer command "sequence" intent.Intent.Sequence
        Sql.integer command "epoch" intent.Intent.Epoch
        Sql.add command "hash" NpgsqlDbType.Bytea (box intent.Intent.EntryHash)

        if command.ExecuteNonQuery() <> 1 then
            raise CaseIdentityCoverageUnknowable

        digest

    let appendIntentDigest
        (hash: IncrementalHash)
        (intent: SubjectOperation)
        (commitment: byte array)
        =
        let sequence = Array.zeroCreate<byte> 8
        BinaryPrimitives.WriteInt64BigEndian(sequence, intent.Intent.Sequence)
        hash.AppendData(sequence)
        hash.AppendData(intent.Intent.EntryHash)
        hash.AppendData(commitment)

    let denialDigest connection transaction caseId =
        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        hash.AppendData(Encoding.ASCII.GetBytes("CLAIMCORE_ERASURE_DENIAL_SET_V2\000"))
        let mutable after: byte array option = None
        let mutable count = 0L
        let mutable more = true

        while more do
            use command =
                new NpgsqlCommand(
                    "SELECT operation_commitment,knowledge,witness_intent_sequence,"
                    + "witness_intent_epoch,witness_intent_entry_hash "
                    + "FROM claimcore.case_erasure_operation_denials "
                    + "WHERE case_id=@case AND (@after IS NULL OR operation_commitment>@after) "
                    + "ORDER BY operation_commitment LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            Sql.optional command "after" NpgsqlDbType.Bytea after
            use reader = command.ExecuteReader()
            let mutable last = None

            while reader.Read() do
                let commitment = reader.GetFieldValue<byte array>(0)
                hash.AppendData(commitment)
                hash.AppendData(Encoding.ASCII.GetBytes(reader.GetString(1)))

                if not (reader.IsDBNull(2)) then
                    let sequence = Array.zeroCreate<byte> 8
                    BinaryPrimitives.WriteInt64BigEndian(sequence, reader.GetInt64(2))
                    hash.AppendData(sequence)
                    hash.AppendData(reader.GetFieldValue<byte array>(4))

                last <- Some commitment
                count <- count + 1L

            match last with
            | None -> more <- false
            | Some digest -> after <- Some digest

        count, hash.GetHashAndReset()

    let scan
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        caseId
        cutoff
        =
        commitments.Admit()
        use intentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        intentHash.AppendData(Encoding.ASCII.GetBytes("CLAIMCORE_ERASURE_CASE_INTENTS_V1\000"))

        let observed =
            witness.EvidenceStore.ReadSubjectOperations(
                caseId,
                cutoff,
                fun page ->
                    for intent in page do
                        requireSource connection transaction intent.OperationId caseId
                        let commitment = retain connection transaction caseId commitments intent
                        appendIntentDigest intentHash intent commitment
            )

        let denialCount, denialRoot = denialDigest connection transaction caseId

        {
            CutoffSequence = observed.CutoffSequence
            CutoffHash = observed.CutoffHash
            IntentCount = observed.IntentCount
            IntentDigest = intentHash.GetHashAndReset()
            DenialCount = denialCount
            DenialDigest = denialRoot
        }
