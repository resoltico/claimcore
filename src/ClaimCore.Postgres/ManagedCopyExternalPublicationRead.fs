namespace ClaimCore.Postgres

open System
open Npgsql

[<NoEquality; NoComparison>]
type internal StoredExternalCopyPublication =
    {
        PublicationId: Guid
        CopyId: Guid
        CaseId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        EncryptionKeyId: Guid
        CiphertextSha256: byte array
        CiphertextBytes: int64
        CapturedAt: DateTimeOffset
        RetainUntil: DateTimeOffset
        LocationCommitment: byte array
        CustodianCommitment: byte array
        RegistryKeyId: Guid
        RegistryHolderId: Guid
        InspectorKeyId: Guid
        InspectorHolderId: Guid
        RegistryCanonical: byte array
        RegistrySignature: byte array
        InspectionCanonical: byte array
        InspectionSignature: byte array
        ActorAuthorityRevision: int64
        CaseRevision: int64
        ExecutorKind: string
        Canonical: byte array
        CandidateHash: byte array
        WitnessSequence: int64
        WitnessHash: byte array
        PublishedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

module internal ManagedCopyExternalPublicationRead =
    let private columns =
        "publication_id,copy_id,case_id,installation_id,lineage_id,witness_epoch,"
        + "encryption_key_id,ciphertext_sha256,ciphertext_bytes,captured_at,retain_until,"
        + "location_commitment,custodian_commitment,registry_signing_key_id,"
        + "registry_holder_actor_id,inspector_signing_key_id,inspector_holder_actor_id,"
        + "registry_canonical,registry_signature,inspection_canonical,inspection_signature,"
        + "actor_authority_revision,case_revision,executor_kind,canonical_action,"
        + "candidate_sha256,witness_sequence,witness_entry_hash,published_at,valid_until"

    let private bytes (reader: Data.Common.DbDataReader) index =
        reader.GetFieldValue<byte array>(index)

    let private read (reader: Data.Common.DbDataReader) =
        {
            PublicationId = reader.GetGuid(0)
            CopyId = reader.GetGuid(1)
            CaseId = reader.GetGuid(2)
            InstallationId = reader.GetGuid(3)
            LineageId = reader.GetGuid(4)
            Epoch = reader.GetInt64(5)
            EncryptionKeyId = reader.GetGuid(6)
            CiphertextSha256 = bytes reader 7
            CiphertextBytes = reader.GetInt64(8)
            CapturedAt = reader.GetFieldValue<DateTimeOffset>(9)
            RetainUntil = reader.GetFieldValue<DateTimeOffset>(10)
            LocationCommitment = bytes reader 11
            CustodianCommitment = bytes reader 12
            RegistryKeyId = reader.GetGuid(13)
            RegistryHolderId = reader.GetGuid(14)
            InspectorKeyId = reader.GetGuid(15)
            InspectorHolderId = reader.GetGuid(16)
            RegistryCanonical = bytes reader 17
            RegistrySignature = bytes reader 18
            InspectionCanonical = bytes reader 19
            InspectionSignature = bytes reader 20
            ActorAuthorityRevision = reader.GetInt64(21)
            CaseRevision = reader.GetInt64(22)
            ExecutorKind = reader.GetString(23)
            Canonical = bytes reader 24
            CandidateHash = bytes reader 25
            WitnessSequence = reader.GetInt64(26)
            WitnessHash = bytes reader 27
            PublishedAt = reader.GetFieldValue<DateTimeOffset>(28)
            ValidUntil = reader.GetFieldValue<DateTimeOffset>(29)
        }

    let private find connection transaction predicate parameter value =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT "
                    + columns
                    + " FROM claimcore.managed_copy_external_publications "
                    + "WHERE "
                    + predicate,
                    connection,
                    transaction
                )

            Sql.uuid command parameter value
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let result = read reader
                return if reader.Read() then None else Some result
        }

    let byPublication connection transaction publicationId =
        find connection transaction "publication_id=@publication" "publication" publicationId

    let byCopy connection transaction copyId =
        find connection transaction "copy_id=@copy" "copy" copyId
