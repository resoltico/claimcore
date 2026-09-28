namespace ClaimCore.Postgres

open Npgsql
open NpgsqlTypes

/// One immutable owner receipt co-committed with its CASE witness intent. No managed-copy
/// projection is manufactured: the external copy remains an unresolved inventory liability.
module internal ManagedCopyExternalPublicationWrite =
    let private sql =
        "INSERT INTO claimcore.managed_copy_external_publications "
        + "(publication_id,copy_id,case_id,installation_id,lineage_id,witness_epoch,"
        + "encryption_key_id,ciphertext_sha256,ciphertext_bytes,captured_at,retain_until,"
        + "location_commitment,custodian_commitment,registry_signing_key_id,"
        + "registry_holder_actor_id,inspector_signing_key_id,inspector_holder_actor_id,"
        + "registry_canonical,registry_signature,inspection_canonical,inspection_signature,"
        + "actor_authority_revision,case_revision,executor_kind,canonical_action,"
        + "candidate_sha256,witness_sequence,witness_entry_hash,published_at,valid_until) "
        + "VALUES (@publication,@copy,@case,@installation,@lineage,@epoch,@encryption,"
        + "@sha,@bytes,@captured,@retain,@location,@custodian,@registryKey,@registryHolder,"
        + "@inspectorKey,@inspectorHolder,@registryCanonical,@registrySignature,"
        + "@inspectionCanonical,@inspectionSignature,@actorRevision,@caseRevision,"
        + "'SCHEMA_OWNER_PROCESS',@canonical,@candidate,@sequence,@entryHash,@observed,@validUntil)"

    let private identity (command: NpgsqlCommand) (value: ExternalCopyPublicationEvidence) =
        let r = value.Registry
        Sql.uuid command "publication" r.PublicationId
        Sql.uuid command "copy" r.CopyId
        Sql.uuid command "case" r.CaseId
        Sql.uuid command "installation" r.InstallationId
        Sql.uuid command "lineage" r.LineageId
        Sql.integer command "epoch" r.Epoch
        Sql.uuid command "encryption" r.EncryptionKeyId
        Sql.add command "sha" NpgsqlDbType.Bytea (box r.CiphertextSha256)
        Sql.integer command "bytes" r.CiphertextBytes
        Sql.add command "captured" NpgsqlDbType.TimestampTz (box r.CapturedAt)
        Sql.add command "retain" NpgsqlDbType.TimestampTz (box r.RetainUntil)
        Sql.add command "location" NpgsqlDbType.Bytea (box r.LocationCommitment)
        Sql.add command "custodian" NpgsqlDbType.Bytea (box r.CustodianCommitment)

    let private signatures (command: NpgsqlCommand) (value: ExternalCopyPublicationEvidence) =
        Sql.uuid command "registryKey" value.Registry.RegistrySigningKeyId
        Sql.uuid command "registryHolder" value.RegistryHolderId
        Sql.uuid command "inspectorKey" value.Inspection.InspectorSigningKeyId
        Sql.uuid command "inspectorHolder" value.InspectorHolderId

        Sql.add
            command
            "registryCanonical"
            NpgsqlDbType.Bytea
            (box value.Submission.Registry.Canonical)

        Sql.add
            command
            "registrySignature"
            NpgsqlDbType.Bytea
            (box value.Submission.Registry.Signature)

        Sql.add
            command
            "inspectionCanonical"
            NpgsqlDbType.Bytea
            (box value.Submission.Inspection.Canonical)

        Sql.add
            command
            "inspectionSignature"
            NpgsqlDbType.Bytea
            (box value.Submission.Inspection.Signature)

    let insert
        connection
        transaction
        (value: ExternalCopyPublicationEvidence)
        canonical
        (intent: WitnessIntent)
        =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            identity command value
            signatures command value
            Sql.integer command "actorRevision" value.ActorAuthorityRevision
            Sql.integer command "caseRevision" value.CaseRevision
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            Sql.add command "observed" NpgsqlDbType.TimestampTz (box value.ObservedAt)
            Sql.add command "validUntil" NpgsqlDbType.TimestampTz (box value.Registry.ValidUntil)
            let! count = command.ExecuteNonQueryAsync()

            if count <> 1 then
                invalidOp "External copy publication did not co-commit."
        }
