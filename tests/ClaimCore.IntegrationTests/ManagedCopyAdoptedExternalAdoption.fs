module internal ClaimCore.IntegrationTests.ManagedCopyAdoptedExternalAdoption

open System
open System.Data
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyAdoptionFixture
open ClaimCore.IntegrationTests.ManagedCopyAdoptionDocuments
open ClaimCore.IntegrationTests.ManagedCopyExternalPublicationFixture
open ClaimCore.IntegrationTests.ManagedCopyAdoptedProductTests

let private ct = CancellationToken.None

let private request
    (source: AdoptionSource)
    caseId
    originKind
    location
    custodian
    copyKeyId
    registryId
    inspectorId
    (submission: CopyAdoptionSubmission)
    (now: DateTimeOffset)
    : CopyAdoptionApprovalRequest =
    {
        ApprovalId = submission.ApprovalId
        AdoptionEventId = submission.AdoptionEventId
        CopyId = source.CopyId
        CaseId = caseId
        Origin = originKind
        CiphertextSha256 = source.Sha256
        CiphertextBytes = source.Bytes
        CapturedAt = source.CapturedAt
        RetainUntil = source.RetainUntil
        LocationCommitment = location
        CustodianCommitment = custodian
        CustodianSigningKeyId = copyKeyId
        RegistrySigningKeyId = registryId
        InspectorSigningKeyId = inspectorId
        CustodianCanonicalSha256 = SHA256.HashData submission.Custodian.Canonical
        RegistryCanonicalSha256 = SHA256.HashData submission.Registry.Canonical
        InspectionReportSha256 = SHA256.HashData submission.Inspection.Canonical
        ExpiresAt = now.AddMinutes(12.0)
    }

let private verifyPreFence
    owner
    (witness: WitnessProtocol)
    (approval: CopyAdoptionApprovalRequest)
    =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted)

    let valid =
        ManagedCopyExternalPublicationOrigin.verify
            connection
            transaction
            witness
            (witness.Snapshot().TipSequence)
            approval
            ct
        |> await

    Expect.isTrue valid "Pre-fence signed external publication is verified"
    transaction.Commit()

let private writeAdoption
    owner
    (witness: WitnessProtocol)
    commitments
    (actor: IActorClaimsCore)
    (publication: PublicationFixture)
    (approval: CopyAdoptionApprovalRequest)
    submission
    =
    match actor.ApproveCopyAdoption(approval, ct) |> await with
    | CopyAdoptionApprovalOutcome.Approved _ -> ()
    | _ -> failtest "External adoption approval failed."

    let privateLocation =
        SyntheticPrivateLocation(
            publication.CiphertextPath,
            publication.CustodianId,
            publication.CommitmentKeyPath
        )
        :> ICopyAdoptionPrivateLocation

    match
        ManagedCopyAdoptionOwner.adopt owner witness commitments privateLocation submission ct
        |> await
    with
    | CopyAdoptionOwnerOutcome.Adopted(id, 1L) when id = submission.AdoptionEventId -> ()
    | _ -> failtest "External signed REGISTER failed."

let private signedRequest
    (witness: WitnessProtocol)
    (source: AdoptionSource)
    caseId
    (publication: PublicationFixture)
    keys
    copyKeyId
    registryId
    inspectorId
    =
    let originKind =
        CopyAdoptionOrigin.AdoptedExternal(source.Sequence, source.EntryHash)

    use commitmentKey = ManagedCopyCommitmentKey.Load(publication.CommitmentKeyPath)
    let location = commitmentKey.Commit("location", publication.CiphertextPath)
    let custodian = commitmentKey.Commit("custodian", publication.CustodianId)
    let now = utcMicrosecond DateTimeOffset.UtcNow

    let submission, _ =
        signedDocsForOrigin
            witness
            source
            originKind
            caseId
            (Guid.NewGuid())
            (Guid.NewGuid())
            location
            custodian
            keys
            now
            (now.AddMinutes(10.0))

    let approval =
        request
            source
            caseId
            originKind
            location
            custodian
            copyKeyId
            registryId
            inspectorId
            submission
            now

    submission, approval

let adopt
    owner
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    (actor: IActorClaimsCore)
    caseId
    (source: AdoptionSource)
    (publication: PublicationFixture)
    commitments
    =
    let keys = signers owner witness runtime proposer

    let copyKey, registryKey, inspectorKey, algorithm, copyKeyId, registryId, inspectorId =
        keys

    use _registryKey = registryKey
    use _inspectorKey = inspectorKey

    let submission, approval =
        signedRequest witness source caseId publication keys copyKeyId registryId inspectorId

    verifyPreFence owner witness approval
    writeAdoption owner witness commitments actor publication approval submission
    let verified, tip = origin owner witness source.CopyId

    Expect.equal
        verified.ProducerKind
        "ADOPTED_EXTERNAL"
        "Verified origin retains external provenance"

    copyKey, algorithm, verified, tip
