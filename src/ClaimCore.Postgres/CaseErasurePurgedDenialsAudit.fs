namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open System.Text
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

/// Read-only proof after the raw case rows are gone. The independent CASE witness metadata
/// still supplies every event ID and ticket; the primary retains only owner-keyed denials.
module internal CaseErasurePurgedDenialsAudit =
    let private requireIntent
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (commitments: ISuppressionCommitments)
        caseId
        (intent: ClaimCore.Witness.SubjectOperation)
        (ct: CancellationToken)
        =
        task {
            let digest = commitments.Operation intent.OperationId

            use command =
                new NpgsqlCommand(
                    "SELECT witness_intent_sequence,witness_intent_epoch,witness_intent_entry_hash "
                    + "FROM claimcore.case_erasure_operation_denials "
                    + "WHERE case_id=@case AND operation_commitment=@digest",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            Sql.add command "digest" NpgsqlDbType.Bytea (box digest)
            use! reader = command.ExecuteReaderAsync(ct)

            let! found = reader.ReadAsync(ct)

            if not found then
                invalidOp "Purged case witness intent denial is missing."

            let matching =
                not (reader.IsDBNull(0))
                && reader.GetInt64(0) = intent.Intent.Sequence
                && reader.GetInt64(1) = intent.Intent.Epoch
                && reader.GetFieldValue<byte array>(2) = intent.Intent.EntryHash

            let! duplicated = reader.ReadAsync(ct)

            if not matching || duplicated then
                invalidOp "Purged case witness intent denial differs."

            return digest
        }

    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        (commitments: ISuppressionCommitments)
        caseId
        cutoff
        (expectedCutoffHash: byte array)
        expectedIntentCount
        (expectedIntentDigest: byte array)
        expectedDenialCount
        (expectedDenialDigest: byte array)
        (ct: CancellationToken)
        =
        task {
            commitments.Admit()
            use intents = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            intents.AppendData(Encoding.ASCII.GetBytes("CLAIMCORE_ERASURE_CASE_INTENTS_V1\000"))

            let! observed =
                witness.EvidenceStore.ReadSubjectOperations(
                    caseId,
                    cutoff,
                    (fun page ->
                        task {
                            for intent in page do
                                let! digest =
                                    requireIntent
                                        connection
                                        transaction
                                        commitments
                                        caseId
                                        intent
                                        ct

                                CaseErasureWitnessDenials.appendIntentDigest intents intent digest
                        }),
                    ct
                )

            let! denialCount, denialDigest =
                CaseErasureWitnessDenials.denialDigest connection transaction caseId ct

            if
                observed.CutoffHash <> expectedCutoffHash
                || observed.IntentCount <> expectedIntentCount
                || intents.GetHashAndReset() <> expectedIntentDigest
                || denialCount <> expectedDenialCount
                || denialDigest <> expectedDenialDigest
            then
                invalidOp "Purged case denial seal differs."

            return denialCount
        }
