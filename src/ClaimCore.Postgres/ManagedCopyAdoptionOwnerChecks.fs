namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Application

[<NoEquality; NoComparison>]
type private AdoptionCheckContext =
    {
        Source: CopyAdoptionOriginState
        Parsed: ParsedCopyAdoptionDocuments
        Stored: StoredCaseTombstone
        ActorRevision: int64
        Now: DateTimeOffset
    }

/// All checks run after the owner has locked actor authority, tombstone and exact approval.
/// No private location, signer claim or pre-fence origin can be inferred from a hash alone.
module internal ManagedCopyAdoptionOwnerChecks =
    let private signatures
        connection
        transaction
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        ownerActorId
        cutoff
        =
        task {
            let! custodian =
                ManagedCopyAdoptionSignatureEvidence.verify
                    connection
                    transaction
                    request.CustodianSigningKeyId
                    "COPY_ATTESTOR"
                    submission.Custodian.Canonical
                    submission.Custodian.Signature
                    cutoff

            let! registry =
                ManagedCopyAdoptionSignatureEvidence.verify
                    connection
                    transaction
                    request.RegistrySigningKeyId
                    "LOCATION_REGISTRY"
                    submission.Registry.Canonical
                    submission.Registry.Signature
                    cutoff

            let! inspector =
                ManagedCopyAdoptionSignatureEvidence.verify
                    connection
                    transaction
                    request.InspectorSigningKeyId
                    "LOCATION_INSPECTOR"
                    submission.Inspection.Canonical
                    submission.Inspection.Signature
                    cutoff

            return
                match custodian, registry, inspector with
                | Some c, Some r, Some i when
                    ManagedCopyAdoptionSignatureEvidence.distinct ownerActorId c r i
                    ->
                    Some(c, r, i)
                | _ -> None
        }

    let private assemble
        (approved: ApprovedCopyAdoption)
        (submission: CopyAdoptionSubmission)
        documents
        signers
        (origin: CopyAdoptionOriginState)
        (stored: StoredCaseTombstone)
        actorRevision
        now
        (proof: VerifiedCopyAdoptionPrivateLocation)
        =
        let custodian, registry, inspector = signers

        let eventHash =
            ManagedCopyEventHash.compute
                origin.PreviousEventHash
                submission.Custodian.Canonical
                (Some submission.Custodian.Signature)

        {
            Approval = approved
            Submission = submission
            Documents = documents
            CustodianSigner = custodian
            RegistrySigner = registry
            InspectorSigner = inspector
            PreviousEventHash = origin.PreviousEventHash
            CopyEventHash = eventHash
            CaseAuthorityRevision = stored.AuthorityRevision
            CaseAuthorityHash = stored.AuthorityHash
            ActorAuthorityRevision = actorRevision
            ObservedAt = now
            PrivateLocationExpiresAt = proof.ExpiresAt
        }

    let private privateProof
        connection
        transaction
        (witness: WitnessProtocol)
        (privateLocation: ICopyAdoptionPrivateLocation)
        (request: CopyAdoptionApprovalRequest)
        (submission: CopyAdoptionSubmission)
        (source: CopyAdoptionOriginState)
        (parsed: ParsedCopyAdoptionDocuments)
        now
        ct
        =
        task {
            let! found =
                privateLocation.Verify(
                    connection,
                    transaction,
                    witness,
                    request,
                    submission,
                    now,
                    ct
                )

            return
                match found with
                | None -> Error CopyAdoptionOwnerOutcome.PrivateLocationUnknown
                | Some proof when
                    ManagedCopyAdoptionDocumentPolicy.matches
                        witness
                        request
                        submission
                        parsed
                        source.PreviousEventHash
                        proof
                        now
                    ->
                    Ok proof
                | Some _ -> Error CopyAdoptionOwnerOutcome.ResourceUnavailable
        }

    let private completed approved submission parsed holders source stored revision now location =
        assemble approved submission parsed holders source stored revision now location
        |> Ok

    let private verifiedDocuments
        connection
        transaction
        (witness: WitnessProtocol)
        (privateLocation: ICopyAdoptionPrivateLocation)
        (approved: ApprovedCopyAdoption)
        (submission: CopyAdoptionSubmission)
        (context: AdoptionCheckContext)
        ct
        =
        task {
            let! signed =
                signatures
                    connection
                    transaction
                    approved.Request
                    submission
                    approved.ActorId
                    (witness.Snapshot().TipSequence)

            match signed with
            | None -> return Error CopyAdoptionOwnerOutcome.ResourceUnavailable
            | Some holders ->
                let! proof =
                    privateProof
                        connection
                        transaction
                        witness
                        privateLocation
                        approved.Request
                        submission
                        context.Source
                        context.Parsed
                        context.Now
                        ct

                match proof with
                | Ok location ->
                    return
                        completed
                            approved
                            submission
                            context.Parsed
                            holders
                            context.Source
                            context.Stored
                            context.ActorRevision
                            context.Now
                            location
                | Error refusal -> return Error refusal
        }

    let private context source parsed stored revision now =
        {
            Source = source
            Parsed = parsed
            Stored = stored
            ActorRevision = revision
            Now = now
        }

    let private prepareApproved
        connection
        transaction
        witness
        privateLocation
        actorRevision
        (stored: StoredCaseTombstone)
        (submission: CopyAdoptionSubmission)
        (approved: ApprovedCopyAdoption)
        now
        ct
        =
        task {
            let! origin =
                ManagedCopyAdoptionOwnerRead.origin connection transaction approved.Request

            match origin, ManagedCopyAdoptionDocumentPolicy.parse submission with
            | Some source, Some parsed when
                source.EncryptionKeyId |> Option.forall ((=) parsed.Custody.EncryptionKeyId)
                ->
                let! originValid =
                    ManagedCopyAdoptionOriginProof.verify
                        connection
                        transaction
                        witness
                        approved.Request
                        parsed.Custody.EncryptionKeyId
                        ct

                if not originValid then
                    return Error CopyAdoptionOwnerOutcome.ResourceUnavailable
                else
                    let valueContext = context source parsed stored actorRevision now

                    return!
                        verifiedDocuments
                            connection
                            transaction
                            witness
                            privateLocation
                            approved
                            submission
                            valueContext
                            ct
            | _ -> return Error CopyAdoptionOwnerOutcome.ResourceUnavailable
        }

    let prepare
        connection
        transaction
        (witness: WitnessProtocol)
        (privateLocation: ICopyAdoptionPrivateLocation)
        actorRevision
        (stored: StoredCaseTombstone)
        (submission: CopyAdoptionSubmission)
        (ct: CancellationToken)
        =
        task {
            let! now = Sql.databaseNow connection transaction

            let! held =
                ManagedCopyTransitionAdministration.held connection transaction (Some stored.CaseId)

            let! approved =
                ManagedCopyAdoptionOwnerApproval.read
                    connection
                    transaction
                    witness
                    actorRevision
                    submission
                    now

            match approved with
            | None -> return Error CopyAdoptionOwnerOutcome.ResourceUnavailable
            | Some _ when stored.Phase <> "ERASURE_PENDING" || held ->
                return Error CopyAdoptionOwnerOutcome.ResourceUnavailable
            | Some value ->
                return!
                    prepareApproved
                        connection
                        transaction
                        witness
                        privateLocation
                        actorRevision
                        stored
                        submission
                        value
                        now
                        ct
        }
