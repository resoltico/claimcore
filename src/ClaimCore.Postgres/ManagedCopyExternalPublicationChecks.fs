namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open ClaimCore.Witness

/// Pre-request publication proves a copy ID and present private bytes were known before the
/// erasure fence. It never claims deletion or turns an unlocated copy into a cleared liability.
module internal ManagedCopyExternalPublicationChecks =
    let private caseRevision connection transaction caseId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT c.revision,c.privacy_phase FROM claimcore.cases c "
                    + "WHERE c.case_id=@case AND NOT EXISTS "
                    + "(SELECT 1 FROM claimcore.case_erasure_tombstones t WHERE t.case_id=c.case_id) "
                    + "FOR UPDATE OF c",
                    connection,
                    transaction
                )

            Sql.uuid command "case" caseId
            use! reader = command.ExecuteReaderAsync()

            if not (reader.Read()) then
                return None
            else
                let revision = reader.GetInt64(0)
                let phase = reader.GetString(1)

                return
                    if phase = "ACTIVE" && not (reader.Read()) then
                        Some revision
                    else
                        None
        }

    let private copyAbsent connection transaction copyId =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT NOT EXISTS(SELECT 1 FROM claimcore.managed_copies WHERE copy_id=@copy) "
                    + "AND NOT EXISTS(SELECT 1 FROM claimcore.managed_copy_external_publications "
                    + "WHERE copy_id=@copy)",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" copyId
            let! value = command.ExecuteScalarAsync()
            return unbox<bool> value
        }

    let private signed
        connection
        transaction
        (witness: WitnessProtocol)
        (registration: ExternalCopyPublication)
        (inspection: ExternalCopyInspection)
        (submission: ExternalCopyPublicationSubmission)
        =
        task {
            let cutoff = witness.Snapshot().TipSequence

            let! registry =
                ManagedCopyAdoptionSignatureEvidence.verify
                    connection
                    transaction
                    registration.RegistrySigningKeyId
                    "LOCATION_REGISTRY"
                    submission.Registry.Canonical
                    submission.Registry.Signature
                    cutoff

            let! inspector =
                ManagedCopyAdoptionSignatureEvidence.verify
                    connection
                    transaction
                    inspection.InspectorSigningKeyId
                    "LOCATION_INSPECTOR"
                    submission.Inspection.Canonical
                    submission.Inspection.Signature
                    cutoff

            return
                match registry, inspector with
                | Some first, Some second when
                    first.HolderId <> second.HolderId && first.KeyId <> second.KeyId
                    ->
                    Some(first, second)
                | _ -> None
        }

    let private exact
        (witness: WitnessProtocol)
        (registration: ExternalCopyPublication)
        (inspection: ExternalCopyInspection)
        (submission: ExternalCopyPublicationSubmission)
        (proof: VerifiedCopyAdoptionPrivateLocation)
        now
        =
        registration.InstallationId = witness.Identity.InstallationId
        && registration.LineageId = witness.Identity.LineageId
        && registration.Epoch = witness.Identity.Epoch
        && inspection.RegistryCanonicalSha256 = SHA256.HashData(submission.Registry.Canonical)
        && registration.IssuedAt <= now
        && registration.ValidUntil > now
        && registration.ValidUntil <= registration.IssuedAt.AddMinutes(10.0)
        && inspection.ObservedAt <= now
        && inspection.ValidUntil > now
        && inspection.ValidUntil <= inspection.ObservedAt.AddMinutes(5.0)
        && registration.RetainUntil > now
        && proof.CopyId = registration.CopyId
        && proof.CaseId = registration.CaseId
        && proof.CiphertextSha256 = registration.CiphertextSha256
        && proof.CiphertextBytes = registration.CiphertextBytes
        && proof.LocationCommitment = registration.LocationCommitment
        && proof.CustodianCommitment = registration.CustodianCommitment
        && proof.ObservedAt >= inspection.ObservedAt
        && proof.ObservedAt <= now
        && proof.ExpiresAt > now

    let private verifyHolders
        connection
        transaction
        caseId
        actorRevision
        (registry: CopyAdoptionSignerEvidence)
        (inspector: CopyAdoptionSignerEvidence)
        ct
        =
        task {
            do!
                DataAuditExternalPublicationHolder.verify
                    connection
                    transaction
                    registry.HolderId
                    caseId
                    actorRevision
                    ct

            do!
                DataAuditExternalPublicationHolder.verify
                    connection
                    transaction
                    inspector.HolderId
                    caseId
                    actorRevision
                    ct
        }

    let private inspect
        connection
        transaction
        (witness: WitnessProtocol)
        (privateLocation: IExternalCopyPublicationPrivateLocation)
        actorRevision
        (submission: ExternalCopyPublicationSubmission)
        (registration: ExternalCopyPublication)
        (inspection: ExternalCopyInspection)
        caseRevision
        (registry: CopyAdoptionSignerEvidence)
        (inspector: CopyAdoptionSignerEvidence)
        now
        ct
        =
        task {
            do!
                verifyHolders
                    connection
                    transaction
                    registration.CaseId
                    actorRevision
                    registry
                    inspector
                    ct

            let! privateProof =
                privateLocation.Verify(connection, transaction, registration, submission, now, ct)

            match privateProof with
            | None -> return Error ExternalCopyPublicationOutcome.PrivateLocationUnknown
            | Some proof when exact witness registration inspection submission proof now ->
                return
                    Ok
                        {
                            Registry = registration
                            Inspection = inspection
                            Submission = submission
                            RegistryHolderId = registry.HolderId
                            InspectorHolderId = inspector.HolderId
                            ActorAuthorityRevision = actorRevision
                            CaseRevision = caseRevision
                            ObservedAt = now
                            PrivateLocationExpiresAt = proof.ExpiresAt
                        }
            | Some _ -> return Error ExternalCopyPublicationOutcome.ResourceUnavailable
        }

    let prepare
        connection
        transaction
        (witness: WitnessProtocol)
        (privateLocation: IExternalCopyPublicationPrivateLocation)
        actorRevision
        (submission: ExternalCopyPublicationSubmission)
        (ct: CancellationToken)
        =
        task {
            let parsed = ManagedCopyExternalPublicationDocuments.parse submission

            match parsed with
            | None -> return Error ExternalCopyPublicationOutcome.ResourceUnavailable
            | Some(registration, inspection) ->
                let! revision = caseRevision connection transaction registration.CaseId
                let! absent = copyAbsent connection transaction registration.CopyId

                let! signers =
                    signed connection transaction witness registration inspection submission

                let! now = ManagedCopySignerPolicy.databaseNow connection transaction

                match revision, signers with
                | Some caseRevision, Some(registry, inspector) when absent ->
                    return!
                        inspect
                            connection
                            transaction
                            witness
                            privateLocation
                            actorRevision
                            submission
                            registration
                            inspection
                            caseRevision
                            registry
                            inspector
                            now
                            ct
                | _ -> return Error ExternalCopyPublicationOutcome.ResourceUnavailable
        }
