namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application
open WitnessProtocolReconciliation

module internal ManagedCopyAdoptionApprovalPolicy =
    let private digest (value: byte array) =
        not (isNull (box value)) && value.Length = 32

    let private origin =
        function
        | CopyAdoptionOrigin.ProductExport(exportId, sequence, hash) ->
            exportId <> Guid.Empty && sequence > 0L && digest hash
        | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) -> sequence > 0L && digest hash

    let private hashes (request: CopyAdoptionApprovalRequest) =
        [
            request.CiphertextSha256
            request.LocationCommitment
            request.CustodianCommitment
            request.CustodianCanonicalSha256
            request.RegistryCanonicalSha256
            request.InspectionReportSha256
        ]
        |> List.forall digest

    let validDraft (request: CopyAdoptionApprovalRequest) =
        request.ApprovalId <> Guid.Empty
        && request.AdoptionEventId <> Guid.Empty
        && request.CopyId <> Guid.Empty
        && request.CaseId <> Guid.Empty
        && origin request.Origin
        && request.CiphertextBytes > 0L
        && hashes request
        && request.CustodianSigningKeyId <> Guid.Empty
        && request.RegistrySigningKeyId <> Guid.Empty
        && request.InspectorSigningKeyId <> Guid.Empty
        && request.CustodianSigningKeyId <> request.RegistrySigningKeyId
        && request.CustodianSigningKeyId <> request.InspectorSigningKeyId
        && request.RegistrySigningKeyId <> request.InspectorSigningKeyId
        && Sql.isUtcMicrosecond request.CapturedAt
        && Sql.isUtcMicrosecond request.RetainUntil
        && Sql.isUtcMicrosecond request.ExpiresAt
        && request.RetainUntil > request.CapturedAt

    let valid (context: ActorCallContext) (request: CopyAdoptionApprovalRequest) =
        context.Action = EndpointAction.ApproveCopyAdoption
        && context.CaseId = Some request.CaseId
        && PrincipalKey.isHuman context.Binding.Principal
        && validDraft request

    let private preFence =
        function
        | CopyAdoptionOrigin.ProductExport(_, sequence, hash)
        | CopyAdoptionOrigin.AdoptedExternal(sequence, hash) -> sequence, hash

    let historical (witness: WitnessProtocol) (stored: StoredCaseTombstone) request ct =
        task {
            let sequence, hash = preFence request.Origin

            if
                sequence >= stored.RequestWitnessSequence
                || request.CapturedAt >= stored.RequestedAt
            then
                return false
            else
                try
                    do! witness.VerifyHistoricalTip(sequence, hash, ct)
                    return true
                with _ ->
                    return false
        }

    let private exportSql =
        "SELECT c.source_case_id,c.producer_kind,c.state,c.revision,c.ciphertext_sha256,"
        + "c.ciphertext_bytes,c.captured_at,c.retain_until,e.artifact_sha256,"
        + "e.witness_sequence,e.witness_entry_hash,e.issued_at "
        + "FROM claimcore.managed_copies c JOIN claimcore.recovery_artifact_exports e "
        + "ON e.export_id=c.product_export_id WHERE c.copy_id=@copy AND e.export_id=@export"

    let private productExport
        connection
        transaction
        (request: CopyAdoptionApprovalRequest)
        exportId
        sequence
        hash
        =
        task {
            use command = new NpgsqlCommand(exportSql, connection, transaction)
            Sql.uuid command "copy" request.CopyId
            Sql.uuid command "export" exportId
            use! reader = command.ExecuteReaderAsync()

            return
                reader.Read()
                && request.CopyId = exportId
                && reader.GetGuid(0) = request.CaseId
                && reader.GetString(1) = "PRODUCT_EXPORT"
                && reader.GetString(2) = "UNKNOWN"
                && reader.GetInt64(3) = 1L
                && reader.GetFieldValue<byte array>(4) = request.CiphertextSha256
                && reader.GetInt64(5) = request.CiphertextBytes
                && reader.GetFieldValue<DateTimeOffset>(6) = request.CapturedAt
                && reader.GetFieldValue<DateTimeOffset>(7) <= request.RetainUntil
                && reader.GetFieldValue<byte array>(8) = request.CiphertextSha256
                && reader.GetInt64(9) = sequence
                && reader.GetFieldValue<byte array>(10) = hash
                && reader.GetFieldValue<DateTimeOffset>(11) = request.CapturedAt
                && not (reader.Read())
        }

    let private externalAbsent connection transaction copyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT NOT EXISTS(SELECT 1 FROM claimcore.managed_copies WHERE copy_id=@copy)",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" copyId
            let! result = command.ExecuteScalarAsync()
            return unbox<bool> result
        }

    let source
        connection
        transaction
        (witness: WitnessProtocol)
        (request: CopyAdoptionApprovalRequest)
        ct
        =
        match request.Origin with
        | CopyAdoptionOrigin.ProductExport(exportId, sequence, hash) ->
            productExport connection transaction request exportId sequence hash
        | CopyAdoptionOrigin.AdoptedExternal _ ->
            task {
                let! absent = externalAbsent connection transaction request.CopyId

                if not absent then
                    return false
                else
                    let! snapshot = witness.Snapshot(ct)

                    return!
                        ManagedCopyExternalPublicationOrigin.verify
                            connection
                            transaction
                            witness
                            snapshot.TipSequence
                            request
                            ct
            }

    let authorized (context: ActorCallContext) (authority: ActorAuthority) caseId =
        let owner =
            authority.Grants
            |> List.exists (fun grant ->
                grant.Role = Role.Owner
                && (grant.Scope = GrantScope.Installation || grant.Scope = GrantScope.Case caseId))

        owner
        && (match
                ActorAuthorization.authorizeAtRevision
                    context.Binding.Principal
                    authority
                    context.Binding.GrantRevision
                    EndpointAction.ApproveCopyAdoption
                    (ResourceScope.Case caseId)
            with
            | AuthorizationDecision.Available(actorId, _) -> actorId = context.Binding.ActorId
            | _ -> false)
