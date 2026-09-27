module internal ClaimCore.IntegrationTests.ManagedCopyAdoptionSource

open System
open Npgsql
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ManagedCopyAdoptionDocuments

let product owner (caseId: Guid) =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT c.copy_id,c.ciphertext_sha256,c.ciphertext_bytes,c.captured_at,"
            + "c.retain_until,e.witness_sequence,e.witness_entry_hash,c.event_hash,"
            + "c.encryption_key_id FROM claimcore.managed_copies c "
            + "JOIN claimcore.recovery_artifact_exports e ON e.export_id=c.product_export_id "
            + "WHERE c.source_case_id=@case AND c.producer_kind='PRODUCT_EXPORT'",
            connection
        )

    Sql.uuid command "case" caseId
    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        invalidOp "Synthetic product export copy is missing."

    let result: AdoptionSource =
        {
            CopyId = reader.GetGuid(0)
            Sha256 = reader.GetFieldValue<byte array>(1)
            Bytes = reader.GetInt64(2)
            CapturedAt = reader.GetFieldValue<DateTimeOffset>(3)
            RetainUntil = reader.GetFieldValue<DateTimeOffset>(4)
            Sequence = reader.GetInt64(5)
            EntryHash = reader.GetFieldValue<byte array>(6)
            PreviousHash = reader.GetFieldValue<byte array>(7)
            EncryptionKeyId = reader.GetGuid(8)
        }

    if reader.Read() then
        invalidOp "Synthetic product export copy is duplicated."

    result
