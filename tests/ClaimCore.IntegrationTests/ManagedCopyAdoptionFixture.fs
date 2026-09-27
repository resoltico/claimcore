module internal ClaimCore.IntegrationTests.ManagedCopyAdoptionFixture

open System
open System.IO
open System.Security.Cryptography
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyAdoptionDocuments
open ClaimCore.IntegrationTests.ManagedCopyAdoptionSource
open ClaimCore.IntegrationTests.ActorGrantTestSupport

type internal SyntheticPrivateLocation(path: string, custodian: string, keyPath: string) =
    interface ICopyAdoptionPrivateLocation with
        member _.Verify(_primary, _transaction, _witness, request, _submission, observedAt, _ct) =
            task {
                use key = ManagedCopyCommitmentKey.Load(keyPath)
                let bytes = File.ReadAllBytes(path)
                let location = key.Commit("location", path)
                let held = key.Commit("custodian", custodian)

                if
                    int64 bytes.Length <> request.CiphertextBytes
                    || SHA256.HashData(bytes) <> request.CiphertextSha256
                    || location <> request.LocationCommitment
                    || held <> request.CustodianCommitment
                then
                    return None
                else
                    return
                        Some(
                            VerifiedCopyAdoptionPrivateLocation.FromVerifiedOpenFile(
                                request.CopyId,
                                request.CaseId,
                                location,
                                held,
                                request.CiphertextSha256,
                                request.CiphertextBytes,
                                observedAt,
                                observedAt.AddMinutes(5.0)
                            )
                        )
            }

[<NoEquality; NoComparison>]
type internal AdoptionFixture =
    {
        Request: CopyAdoptionApprovalRequest
        Submission: CopyAdoptionSubmission
        PrivateLocation: ICopyAdoptionPrivateLocation
        CiphertextPath: string
        CommitmentKeyPath: string
        CustodianId: string
        TransitionSigningKey: Key
        SignatureAlgorithm: SignatureAlgorithm
    }

let signers owner (witness: WitnessProtocol) runtime proposer =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    let custodian = human "adoption-custodian"
    let inspector = human "adoption-inspector"
    grantCustodian runtime proposer custodian
    grantCustodian runtime proposer inspector

    let copyKey, algorithm, copyId, _ =
        registeredSigner
            runtime
            proposer
            custodian
            CopySignerPurpose.CopyAttestor
            witness
            connection

    let registryKey, _, registryId, _ =
        registeredSigner
            runtime
            proposer
            custodian
            CopySignerPurpose.LocationRegistry
            witness
            connection

    let inspectorKey, _, inspectorId, _ =
        registeredSigner
            runtime
            proposer
            inspector
            CopySignerPurpose.LocationInspector
            witness
            connection

    copyKey, registryKey, inspectorKey, algorithm, copyId, registryId, inspectorId

let private request
    (copy: AdoptionSource)
    (submission: CopyAdoptionSubmission)
    (caseId: Guid)
    location
    custodian
    copyKeyId
    registryKeyId
    inspectorKeyId
    expires
    : CopyAdoptionApprovalRequest =
    {
        ApprovalId = submission.ApprovalId
        AdoptionEventId = submission.AdoptionEventId
        CopyId = copy.CopyId
        CaseId = caseId
        Origin = CopyAdoptionOrigin.ProductExport(copy.CopyId, copy.Sequence, copy.EntryHash)
        CiphertextSha256 = copy.Sha256
        CiphertextBytes = copy.Bytes
        CapturedAt = copy.CapturedAt
        RetainUntil = copy.RetainUntil
        LocationCommitment = location
        CustodianCommitment = custodian
        CustodianSigningKeyId = copyKeyId
        RegistrySigningKeyId = registryKeyId
        InspectorSigningKeyId = inspectorKeyId
        CustodianCanonicalSha256 = SHA256.HashData submission.Custodian.Canonical
        RegistryCanonicalSha256 = SHA256.HashData submission.Registry.Canonical
        InspectionReportSha256 = SHA256.HashData submission.Inspection.Canonical
        ExpiresAt = expires
    }

let private databaseInstant owner =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use command = new NpgsqlCommand("SELECT clock_timestamp()", connection)

    match command.ExecuteScalar() with
    | :? DateTimeOffset as value -> value
    | :? DateTime as value when value.Kind = DateTimeKind.Utc -> DateTimeOffset value
    | _ -> invalidOp "Synthetic primary clock is unavailable."

let fixtureDetails owner (witness: WitnessProtocol) runtime proposer (caseId: Guid) artifact =
    let root = privateRoot ()
    let location = privateBytes root "adopted-export.bin" artifact

    let keyPath =
        privateBytes root "copy-commitment.key" (RandomNumberGenerator.GetBytes(32))

    use key = ManagedCopyCommitmentKey.Load(keyPath)
    let custodian = "synthetic-custodian"
    let locationCommitment = key.Commit("location", location)
    let custodianCommitment = key.Commit("custodian", custodian)
    let copy = product owner caseId
    let keys = signers owner witness runtime proposer
    let copyKey, registryKey, inspectorKey, algorithm, _, _, _ = keys
    use _registryKey = registryKey
    use _inspectorKey = inspectorKey
    let now = databaseInstant owner

    let submission, (copyKeyId, registryKeyId, inspectorKeyId) =
        signedDocs
            witness
            copy
            caseId
            (Guid.NewGuid())
            (Guid.NewGuid())
            locationCommitment
            custodianCommitment
            keys
            now
            (now.AddMinutes(10.0))

    {
        Request =
            request
                copy
                submission
                caseId
                locationCommitment
                custodianCommitment
                copyKeyId
                registryKeyId
                inspectorKeyId
                (now.AddMinutes(12.0))
        Submission = submission
        PrivateLocation =
            SyntheticPrivateLocation(location, custodian, keyPath) :> ICopyAdoptionPrivateLocation
        CiphertextPath = location
        CommitmentKeyPath = keyPath
        CustodianId = custodian
        TransitionSigningKey = copyKey
        SignatureAlgorithm = algorithm
    }

let fixture owner witness runtime proposer caseId artifact =
    let value = fixtureDetails owner witness runtime proposer caseId artifact
    use _key = value.TransitionSigningKey
    value.Request, value.Submission, value.PrivateLocation
