module internal ClaimCore.IntegrationTests.ManagedCopyExternalPublicationFixture

open System
open System.IO
open System.Security.Cryptography
open System.Threading.Tasks
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyExternalPublicationDocuments

[<NoEquality; NoComparison>]
type internal PublicationFixture =
    {
        Submission: ExternalCopyPublicationSubmission
        PrivateLocation: IExternalCopyPublicationPrivateLocation
        RegistryDocumentPath: string
        InspectionDocumentPath: string
        CiphertextPath: string
        CommitmentKeyPath: string
        CustodianId: string
        CopyId: Guid
        CaseId: Guid
    }

type private SyntheticPublicationLocation(path: string, custodian: string, keyPath: string) =
    interface IExternalCopyPublicationPrivateLocation with
        member _.Verify(_, _, publication, _, observedAt, _) =
            task {
                use key = ManagedCopyCommitmentKey.Load(keyPath)
                let bytes = File.ReadAllBytes(path)
                let location = key.Commit("location", path)
                let holder = key.Commit("custodian", custodian)

                if
                    int64 bytes.Length <> publication.CiphertextBytes
                    || SHA256.HashData(bytes) <> publication.CiphertextSha256
                    || location <> publication.LocationCommitment
                    || holder <> publication.CustodianCommitment
                then
                    return None
                else
                    return
                        Some(
                            VerifiedCopyAdoptionPrivateLocation.FromVerifiedOpenFile(
                                publication.CopyId,
                                publication.CaseId,
                                location,
                                holder,
                                publication.CiphertextSha256,
                                publication.CiphertextBytes,
                                observedAt,
                                observedAt.AddMinutes(5.0)
                            )
                        )
            }

let private now owner =
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    use command = new NpgsqlCommand("SELECT clock_timestamp()", connection)

    match command.ExecuteScalar() with
    | :? DateTimeOffset as instant -> instant
    | :? DateTime as instant when instant.Kind = DateTimeKind.Utc -> DateTimeOffset instant
    | _ -> invalidOp "Synthetic primary clock is unavailable."

let private register owner witness runtime proposer (copyId: Guid) =
    let registryHolder = human ("external-publication-registry-" + copyId.ToString("N"))

    let inspectorHolder =
        human ("external-publication-inspector-" + copyId.ToString("N"))

    grantCustodian runtime proposer registryHolder
    grantCustodian runtime proposer inspectorHolder
    use ownerConnection = new NpgsqlConnection(owner)
    ownerConnection.Open()

    let registryKey, algorithm, registryId, _ =
        registeredSigner
            runtime
            proposer
            registryHolder
            CopySignerPurpose.LocationRegistry
            witness
            ownerConnection

    let inspectorKey, _, inspectorId, _ =
        registeredSigner
            runtime
            proposer
            inspectorHolder
            CopySignerPurpose.LocationInspector
            witness
            ownerConnection

    registryKey, inspectorKey, algorithm, registryId, inspectorId

let private signed
    root
    (registryKey: Key)
    (inspectorKey: Key)
    (algorithm: SignatureAlgorithm)
    publicationId
    registry
    inspection
    =
    let registryPath =
        privateBytes root "external-registry.json" (envelope algorithm registryKey registry)

    let inspectionPath =
        privateBytes root "external-inspection.json" (envelope algorithm inspectorKey inspection)

    ({
        PublicationId = publicationId
        Registry =
            {
                Canonical = registry
                Signature = algorithm.Sign(registryKey, registry)
            }
        Inspection =
            {
                Canonical = inspection
                Signature = algorithm.Sign(inspectorKey, inspection)
            }
    }
    : ExternalCopyPublicationSubmission),
    registryPath,
    inspectionPath

let private documents
    root
    (witness: WitnessProtocol)
    (registryKey: Key)
    (inspectorKey: Key)
    (algorithm: SignatureAlgorithm)
    (registryId: Guid)
    (inspectorId: Guid)
    (copyId: Guid)
    (caseId: Guid)
    (ciphertextPath: string)
    (location: byte array)
    (custodian: byte array)
    (issued: DateTimeOffset)
    (retention: TimeSpan)
    =
    let publicationId = Guid.NewGuid()
    let keyId = Guid.NewGuid()
    let bytes = File.ReadAllBytes(ciphertextPath)
    let sha = SHA256.HashData(bytes)
    let captured = issued.AddMinutes(-1.0)
    let retained = issued.Add(retention)

    let registry =
        registryBody
            witness
            publicationId
            copyId
            caseId
            keyId
            sha
            (int64 bytes.Length)
            captured
            retained
            location
            custodian
            registryId
            issued

    let inspection =
        inspectionBody
            publicationId
            copyId
            caseId
            sha
            (int64 bytes.Length)
            location
            (SHA256.HashData registry)
            inspectorId
            issued

    signed root registryKey inspectorKey algorithm publicationId registry inspection

let private privateCopy root =
    let ciphertextPath =
        privateBytes root "external-copy.bin" (RandomNumberGenerator.GetBytes(96))

    let commitmentKeyPath =
        privateBytes root "copy-commitment.key" (RandomNumberGenerator.GetBytes(32))

    ciphertextPath, commitmentKeyPath, "synthetic-external-custodian"

let createWithRetention
    owner
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    caseId
    retention
    =
    let root = privateRoot ()

    let ciphertextPath, commitmentKeyPath, custodianId = privateCopy root
    let copyId = Guid.NewGuid()
    use commitmentKey = ManagedCopyCommitmentKey.Load(commitmentKeyPath)
    let location = commitmentKey.Commit("location", ciphertextPath)
    let custodian = commitmentKey.Commit("custodian", custodianId)

    let registryKey, inspectorKey, algorithm, registryId, inspectorId =
        register owner witness runtime proposer copyId

    use registrySigner = registryKey
    use inspectorSigner = inspectorKey

    let submission, registryPath, inspectionPath =
        documents
            root
            witness
            registryKey
            inspectorKey
            algorithm
            registryId
            inspectorId
            copyId
            caseId
            ciphertextPath
            location
            custodian
            (now owner)
            retention

    {
        Submission = submission
        PrivateLocation =
            SyntheticPublicationLocation(ciphertextPath, custodianId, commitmentKeyPath)
            :> IExternalCopyPublicationPrivateLocation
        RegistryDocumentPath = registryPath
        InspectionDocumentPath = inspectionPath
        CiphertextPath = ciphertextPath
        CommitmentKeyPath = commitmentKeyPath
        CustodianId = custodianId
        CopyId = copyId
        CaseId = caseId
    }

let create owner witness runtime proposer caseId =
    createWithRetention owner witness runtime proposer caseId (TimeSpan.FromDays(1.0))
