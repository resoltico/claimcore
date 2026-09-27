namespace ClaimCore.Postgres

open System
open Npgsql
open NpgsqlTypes
open ClaimCore.Application

/// The receipt is append-only metadata and exact detached signatures; original export
/// provenance remains untouched. One-use human approval joins the same primary transaction.
module internal ManagedCopyAdoptionOwnerReceiptWrite =
    let private sql =
        "INSERT INTO claimcore.managed_copy_adoptions "
        + "(adoption_event_id,copy_id,case_id,origin_kind,export_id,copy_revision,"
        + "ciphertext_sha256,ciphertext_bytes,captured_at,retain_until,pre_fence_kind,"
        + "pre_fence_sequence,pre_fence_hash,location_commitment,custodian_commitment,"
        + "custodian_signing_key_id,registry_signing_key_id,inspector_signing_key_id,"
        + "custodian_holder_actor_id,registry_holder_actor_id,inspector_holder_actor_id,"
        + "owner_actor_id,owner_grant_revision,owner_approval_id,executor_kind,"
        + "actor_authority_revision,custodian_canonical,custodian_signature,"
        + "registry_canonical,registry_signature,inspection_canonical,inspection_signature,"
        + "inspection_report_sha256,canonical_action,candidate_sha256,previous_copy_hash,"
        + "copy_event_hash,witness_sequence,witness_epoch,witness_entry_hash,"
        + "adopted_at,valid_until) VALUES "
        + "(@event,@copy,@case,@origin,@export,@revision,@sha,@bytes,@captured,@retain,"
        + "@preKind,@preSequence,@preHash,@location,@custodian,@custodianKey,@registryKey,"
        + "@inspectorKey,@custodianHolder,@registryHolder,@inspectorHolder,@actor,@grant,"
        + "@approval,'SCHEMA_OWNER_PROCESS',@actorRevision,@custodianCanonical,"
        + "@custodianSignature,@registryCanonical,@registrySignature,@inspectionCanonical,"
        + "@inspectionSignature,@inspectionHash,@canonical,@candidate,@previous,@copyHash,"
        + "@sequence,@epoch,@entryHash,@observed,@validUntil)"

    let private origin (command: NpgsqlCommand) =
        function
        | CopyAdoptionOrigin.ProductExport(exportId, sequence, hash) ->
            Sql.text command "origin" "PRODUCT_EXPORT"
            Sql.uuid command "export" exportId
            Sql.text command "preKind" "PRODUCT_EXPORT_RECEIPT"
            Sql.integer command "preSequence" sequence
            Sql.add command "preHash" NpgsqlDbType.Bytea (box hash)
        | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) ->
            Sql.text command "origin" "ADOPTED_EXTERNAL"
            Sql.add command "export" NpgsqlDbType.Uuid (box DBNull.Value)
            Sql.text command "preKind" "PUBLISHED_REGISTRY"
            Sql.integer command "preSequence" sequence
            Sql.add command "preHash" NpgsqlDbType.Bytea (box hash)

    let private identity (command: NpgsqlCommand) (value: CopyAdoptionOwnerEventData) =
        let request = value.Approval.Request
        Sql.uuid command "event" value.Submission.AdoptionEventId
        Sql.uuid command "copy" request.CopyId
        Sql.uuid command "case" request.CaseId
        origin command request.Origin
        Sql.integer command "revision" value.Documents.Custody.Revision
        Sql.add command "sha" NpgsqlDbType.Bytea (box request.CiphertextSha256)
        Sql.integer command "bytes" request.CiphertextBytes
        Sql.add command "captured" NpgsqlDbType.TimestampTz (box request.CapturedAt)
        Sql.add command "retain" NpgsqlDbType.TimestampTz (box request.RetainUntil)
        Sql.add command "location" NpgsqlDbType.Bytea (box request.LocationCommitment)
        Sql.add command "custodian" NpgsqlDbType.Bytea (box request.CustodianCommitment)

    let private authority (command: NpgsqlCommand) (value: CopyAdoptionOwnerEventData) =
        let request = value.Approval.Request
        Sql.uuid command "custodianKey" request.CustodianSigningKeyId
        Sql.uuid command "registryKey" request.RegistrySigningKeyId
        Sql.uuid command "inspectorKey" request.InspectorSigningKeyId
        Sql.uuid command "custodianHolder" value.CustodianSigner.HolderId
        Sql.uuid command "registryHolder" value.RegistrySigner.HolderId
        Sql.uuid command "inspectorHolder" value.InspectorSigner.HolderId
        Sql.uuid command "actor" value.Approval.ActorId
        Sql.integer command "grant" value.Approval.GrantRevision
        Sql.uuid command "approval" request.ApprovalId
        Sql.integer command "actorRevision" value.ActorAuthorityRevision

    let private documents (command: NpgsqlCommand) (value: CopyAdoptionOwnerEventData) =
        let submission = value.Submission
        Sql.add command "custodianCanonical" NpgsqlDbType.Bytea (box submission.Custodian.Canonical)
        Sql.add command "custodianSignature" NpgsqlDbType.Bytea (box submission.Custodian.Signature)
        Sql.add command "registryCanonical" NpgsqlDbType.Bytea (box submission.Registry.Canonical)
        Sql.add command "registrySignature" NpgsqlDbType.Bytea (box submission.Registry.Signature)

        Sql.add
            command
            "inspectionCanonical"
            NpgsqlDbType.Bytea
            (box submission.Inspection.Canonical)

        Sql.add
            command
            "inspectionSignature"
            NpgsqlDbType.Bytea
            (box submission.Inspection.Signature)

        Sql.add
            command
            "inspectionHash"
            NpgsqlDbType.Bytea
            (box value.Approval.Request.InspectionReportSha256)

    let insert connection transaction value canonical (intent: WitnessIntent) =
        task {
            use command = new NpgsqlCommand(sql, connection, transaction)
            identity command value
            authority command value
            documents command value
            Sql.add command "canonical" NpgsqlDbType.Bytea (box canonical)
            Sql.add command "candidate" NpgsqlDbType.Bytea (box intent.CandidateHash)
            Sql.add command "previous" NpgsqlDbType.Bytea (box value.PreviousEventHash)
            Sql.add command "copyHash" NpgsqlDbType.Bytea (box value.CopyEventHash)
            Sql.integer command "sequence" intent.Ticket.Sequence
            Sql.integer command "epoch" intent.Ticket.Epoch
            Sql.add command "entryHash" NpgsqlDbType.Bytea (box intent.Ticket.EntryHash)
            Sql.add command "observed" NpgsqlDbType.TimestampTz (box value.ObservedAt)

            Sql.add
                command
                "validUntil"
                NpgsqlDbType.TimestampTz
                (box value.Documents.Custody.ValidUntil)

            let! count = command.ExecuteNonQueryAsync()

            if count <> 1 then
                invalidOp "Owner copy adoption receipt did not co-commit."
        }

    let useApproval connection transaction approvalId eventId caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "INSERT INTO claimcore.managed_copy_adoption_approval_uses "
                    + "(approval_id,adoption_event_id,case_id) VALUES (@approval,@event,@case)",
                    connection,
                    transaction
                )

            Sql.uuid command "approval" approvalId
            Sql.uuid command "event" eventId
            Sql.uuid command "case" caseId
            let! count = command.ExecuteNonQueryAsync()

            if count <> 1 then
                invalidOp "Owner copy adoption approval was not consumed."
        }
