module internal ClaimCore.IntegrationTests.ManagedCopyAdoptedDeletionFixture

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyExternalPublicationFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryDocuments
open ClaimCore.IntegrationTests.ManagedCopyVerifiedDeletionFixture
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.ManagedCopyAdoptedProductTests
open ClaimCore.IntegrationTests.ManagedCopyAdoptedAbsenceCheck
open ClaimCore.IntegrationTests.ManagedCopyAdoptedAbsenceDocuments
open ClaimCore.IntegrationTests.ManagedCopyAdoptedDeletionProcess
open ClaimCore.IntegrationTests.ManagedCopyAdoptedDeletionRequest

let private ct = CancellationToken.None

let private approval
    (runtime: Runtime)
    verifierPrincipal
    verifierKeyId
    (origin: VerifiedCopyAdoptionOrigin)
    (tip: Snapshot)
    (report: byte array)
    eventId
    =
    let request: CopyDeletionApprovalRequest =
        {
            ApprovalId = Guid.NewGuid()
            DeletionEventId = eventId
            CopyId = origin.CopyId
            VerifierSigningKeyId = verifierKeyId
            ExpectedCopyRevision = origin.CopyRevision + 2L
            LocationCommitment = origin.LocationCommitment
            InspectionReportSha256 = SHA256.HashData report
            WitnessCutoffSequence = tip.TipSequence
            WitnessCutoffHash = tip.TipHash
            ExpiresAt = utcMicrosecond (DateTimeOffset.UtcNow.AddMinutes(15.0))
        }

    match (runtime.ForActor verifierPrincipal).ApproveCopyDeletion(request, ct) |> await with
    | CopyDeletionApprovalOutcome.Approved _ -> request
    | _ -> failtest "Independent adopted deletion approval failed."

let private deletionTransition
    (request: AdoptedCopyTransition)
    prior
    requestCanonical
    requestSignature
    (cutoff: Snapshot)
    (approved: CopyDeletionApprovalRequest)
    observed
    deletionEventId
    =
    let pendingHash =
        ManagedCopyEventHash.compute prior requestCanonical (Some requestSignature)

    { request with
        EventId = deletionEventId
        Revision = request.Revision + 1L
        EventKind = "VERIFIED_DELETED"
        State = "VERIFIED_DELETED"
        PreviousEventHash = pendingHash
        ActionWitnessCutoffSequence = cutoff.TipSequence
        ActionWitnessCutoffHash = cutoff.TipHash
        LastVerifiedAt = Some observed
        DeletionProofSha256 = Some approved.InspectionReportSha256
        DeletionApprovalId = Some approved.ApprovalId
    }

let private inspectAbsence
    directory
    owner
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    (origin: VerifiedCopyAdoptionOrigin)
    (publication: PublicationFixture)
    =
    use signerConnection = new NpgsqlConnection(owner)
    signerConnection.Open()

    let (extraCopyKey,
         registryKey,
         verifierKey,
         signingAlgorithm,
         _,
         registryKeyId,
         verifierKeyId,
         verifierPrincipal) =
        keys runtime proposer witness signerConnection

    use _extraCopyKey = extraCopyKey
    use _registryKey = registryKey
    use _verifierKey = verifierKey
    let cutoff = witness.Snapshot()

    let registryPath, reportPath, report, observed =
        signedAbsence
            directory
            cutoff
            origin
            publication
            registryKeyId
            verifierKeyId
            registryKey
            verifierKey
            signingAlgorithm

    cutoff, registryPath, reportPath, report, observed, verifierKeyId, verifierPrincipal

let private applyVerified
    directory
    owner
    writer
    witness
    (publication: PublicationFixture)
    (copyKey: Key)
    (algorithm: SignatureAlgorithm)
    commitments
    registryPath
    reportPath
    (verified: AdoptedCopyTransition)
    =
    let canonical = ManagedCopyAdoptedTransitionAttestation.encode verified
    let signature = algorithm.Sign(copyKey, canonical)

    ManagedCopyAdoptedDeletionProcess.execute
        directory
        owner
        writer
        witness
        registryPath
        reportPath
        publication.CommitmentKeyPath
        canonical
        signature

    audit owner witness commitments

let private approvedTransition
    (runtime: Runtime)
    verifierPrincipal
    verifierKeyId
    (origin: VerifiedCopyAdoptionOrigin)
    (cutoff: Snapshot)
    report
    (request: AdoptedCopyTransition)
    prior
    requestCanonical
    requestSignature
    observed
    =
    let eventId = Guid.NewGuid()

    let approved =
        approval runtime verifierPrincipal verifierKeyId origin cutoff report eventId

    deletionTransition
        request
        prior
        requestCanonical
        requestSignature
        cutoff
        approved
        observed
        eventId

let private prepareVerified
    directory
    owner
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    (publication: PublicationFixture)
    (origin: VerifiedCopyAdoptionOrigin)
    (request: AdoptedCopyTransition)
    prior
    requestCanonical
    requestSignature
    =
    let (cutoff, registryPath, reportPath, report, observed, verifierKeyId, verifierPrincipal) =
        inspectAbsence directory owner witness runtime proposer origin publication

    let verified =
        approvedTransition
            runtime
            verifierPrincipal
            verifierKeyId
            origin
            cutoff
            report
            request
            prior
            requestCanonical
            requestSignature
            observed

    registryPath, reportPath, verified

let private beginDeletion
    owner
    witness
    origin
    unknown
    unknownCanonical
    unknownSignature
    copyKey
    algorithm
    =
    let directory = privateRoot ()

    let prior, request, canonical, signature =
        ManagedCopyAdoptedDeletionRequest.create
            owner
            witness
            origin
            unknown
            unknownCanonical
            unknownSignature
            copyKey
            algorithm

    directory, prior, request, canonical, signature

let finish
    owner
    writer
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    (publication: PublicationFixture)
    (origin: VerifiedCopyAdoptionOrigin)
    (unknown: AdoptedCopyTransition)
    unknownCanonical
    unknownSignature
    (copyKey: Key)
    (algorithm: SignatureAlgorithm)
    commitments
    =
    let directory, prior, request, requestCanonical, requestSignature =
        beginDeletion
            owner
            witness
            origin
            unknown
            unknownCanonical
            unknownSignature
            copyKey
            algorithm

    let registryPath, reportPath, verified =
        prepareVerified
            directory
            owner
            witness
            runtime
            proposer
            publication
            origin
            request
            prior
            requestCanonical
            requestSignature

    applyVerified
        directory
        owner
        writer
        witness
        publication
        copyKey
        algorithm
        commitments
        registryPath
        reportPath
        verified
