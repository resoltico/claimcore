module internal ClaimCore.IntegrationTests.ManagedCopyVerifiedDeletionFixture

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Database
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryDocuments

[<NoEquality; NoComparison>]
type internal PendingSyntheticCopy =
    {
        CopyId: Guid
        CustodianId: string
        CopyPath: string
        Ciphertext: byte array
        KeyPath: string
        Registration: byte array
        RegistrationSignature: byte array
        DeleteRequest: byte array
        DeleteSignature: byte array
    }

let keys (runtime: Runtime) owner witness connection =
    let copyHolder = human "delete-copy-holder"
    let registryHolder = human "delete-registry-holder"
    let verifierHolder = human "delete-independent-verifier"

    for principal in [ copyHolder; registryHolder; verifierHolder ] do
        grantCustodian runtime owner principal

    let copyKey, algorithm, copyKeyId, _ =
        registeredSigner runtime owner copyHolder CopySignerPurpose.CopyAttestor witness connection

    let registryKey, _, registryKeyId, _ =
        registeredSigner
            runtime
            owner
            registryHolder
            CopySignerPurpose.LocationRegistry
            witness
            connection

    let verifierKey, _, verifierKeyId, _ =
        registeredSigner
            runtime
            owner
            verifierHolder
            CopySignerPurpose.DeletionVerifier
            witness
            connection

    copyKey,
    registryKey,
    verifierKey,
    algorithm,
    copyKeyId,
    registryKeyId,
    verifierKeyId,
    verifierHolder

let pendingCopy owner connection witness (copyKey: Key) algorithm copyKeyId directory =
    let retain = DateTimeOffset.UtcNow.AddSeconds(4.)

    let copyId, custodianId, copyPath, ciphertext, keyPath, _, registration, signature =
        registerCopyUntil
            owner
            connection
            witness
            copyKey
            algorithm
            copyKeyId
            directory
            (Some retain)

    Thread.Sleep(4500)
    let eventId = Guid.NewGuid()

    let previous =
        ManagedCopyEventHash.compute (Array.zeroCreate<byte> 32) registration (Some signature)

    let deleteRequest =
        transitionFromRegister
            registration
            eventId
            2
            "DELETE_REQUEST"
            "DELETE_PENDING"
            previous
            (witness.Snapshot())

    let deleteSignature = algorithm.Sign(copyKey, deleteRequest)

    match
        ManagedCopyTransitionAdministration.transition
            connection
            witness
            deleteRequest
            deleteSignature
        |> await
    with
    | AuthorityWriteOutcome.Applied(id, 2L) when id = eventId -> ()
    | _ -> failtest "Synthetic deletion request was not witnessed."

    {
        CopyId = copyId
        CustodianId = custodianId
        CopyPath = copyPath
        Ciphertext = ciphertext
        KeyPath = keyPath
        Registration = registration
        RegistrationSignature = signature
        DeleteRequest = deleteRequest
        DeleteSignature = deleteSignature
    }

let inspection
    directory
    tip
    registryKeyId
    verifierKeyId
    copyId
    custodianId
    copyPath
    ciphertext
    (registryKey: Key)
    (verifierKey: Key)
    algorithm
    =
    let now = DateTimeOffset.UtcNow

    let registry =
        registryBodyForEntries
            tip
            registryKeyId
            [ baseEntry copyId custodianId copyPath ciphertext ]
            []
            now

    let report =
        inspectionBodyForObservations
            tip
            verifierKeyId
            (digest registry)
            [ absentObservation copyId ]
            now

    let registryPath =
        privateBytes directory "registry.json" (envelope algorithm registryKey registry)

    let reportPath =
        privateBytes directory "inspection.json" (envelope algorithm verifierKey report)

    registryPath, reportPath, report, now

let approve
    (runtime: Runtime)
    verifierPrincipal
    verifierKeyId
    copyId
    (registration: byte array)
    (report: byte array)
    (tip: Snapshot)
    deletionEventId
    =
    let copy = ManagedCopyRegistrationAttestation.parse registration |> Option.get

    let approval: CopyDeletionApprovalRequest =
        {
            ApprovalId = Guid.NewGuid()
            DeletionEventId = deletionEventId
            CopyId = copyId
            VerifierSigningKeyId = verifierKeyId
            ExpectedCopyRevision = 2L
            LocationCommitment = copy.LocationCommitment
            InspectionReportSha256 = SHA256.HashData(report)
            WitnessCutoffSequence = tip.TipSequence
            WitnessCutoffHash = tip.TipHash
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15.)
        }

    match
        (runtime.ForActor verifierPrincipal).ApproveCopyDeletion(approval, CancellationToken.None)
        |> await
    with
    | CopyDeletionApprovalOutcome.Approved(id, revision) when
        id = approval.ApprovalId && revision > 0L
        ->
        approval
    | _ -> failtest "Independent deletion approval was not witnessed."

let prepareEvidence
    runtime
    verifierPrincipal
    directory
    tip
    registryKeyId
    verifierKeyId
    copyId
    custodianId
    copyPath
    ciphertext
    registryKey
    verifierKey
    algorithm
    registration
    =
    let registryPath, reportPath, report, observedAt =
        inspection
            directory
            tip
            registryKeyId
            verifierKeyId
            copyId
            custodianId
            copyPath
            ciphertext
            registryKey
            verifierKey
            algorithm

    let approval =
        approve
            runtime
            verifierPrincipal
            verifierKeyId
            copyId
            registration
            report
            tip
            (Guid.NewGuid())

    registryPath, reportPath, observedAt, approval
