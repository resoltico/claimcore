module internal ClaimCore.IntegrationTests.ManagedCopyAdoptedExternalSource

open System
open Expecto
open Npgsql
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ManagedCopyAdoptionDocuments

let published owner copyId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT ciphertext_sha256,ciphertext_bytes,captured_at,retain_until,"
            + "witness_sequence,witness_entry_hash,encryption_key_id "
            + "FROM claimcore.managed_copy_external_publications WHERE copy_id=@copy",
            connection
        )

    Sql.uuid command "copy" copyId
    use reader = command.ExecuteReader()

    if not (reader.Read()) then
        failtest "Synthetic external publication is missing"

    let source: AdoptionSource =
        {
            CopyId = copyId
            Sha256 = reader.GetFieldValue<byte array>(0)
            Bytes = reader.GetInt64(1)
            CapturedAt = reader.GetFieldValue<DateTimeOffset>(2)
            RetainUntil = reader.GetFieldValue<DateTimeOffset>(3)
            Sequence = reader.GetInt64(4)
            EntryHash = reader.GetFieldValue<byte array>(5)
            PreviousHash = Array.zeroCreate<byte> 32
            EncryptionKeyId = reader.GetGuid(6)
        }

    if reader.Read() then
        failtest "Synthetic external publication is duplicated"

    source
