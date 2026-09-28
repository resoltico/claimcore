module ClaimCore.IntegrationTests.ManagedCopyInventoryTests

open System
open System.IO
open System.Text.Json
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Postgres
open ClaimCore.Application
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryDocuments
open ClaimCore.Witness

let private assertRefusals
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (caseId: Guid)
    (tip: Snapshot)
    (algorithm: SignatureAlgorithm)
    (registryKey: Key)
    (inspectorKey: Key)
    (registryBody: byte array)
    (inspectionBody: byte array)
    (observation: byte array)
    (registryPath: string)
    (inspectionPath: string)
    (registryEnvelope: byte array)
    (inspectionEnvelope: byte array)
    =
    let inspect () =
        seal connection witness caseId tip.TipSequence tip.TipHash

    let forged = envelope algorithm registryKey inspectionBody
    File.WriteAllBytes(inspectionPath, forged)
    Expect.isNone (inspect ()) "Wrong inspector signature is refused."
    File.WriteAllBytes(inspectionPath, inspectionEnvelope)

    let missing = changed registryBody [ "entries", element Array.empty<JsonElement> ]
    File.WriteAllBytes(registryPath, envelope algorithm registryKey missing)
    Expect.isNone (inspect ()) "Omitted global backup copy is refused."
    File.WriteAllBytes(registryPath, registryEnvelope)

    let absent =
        changed observation [ "status", element "ABSENT"; "sha256", nil; "bytes", nil ]

    use absentDocument = JsonDocument.Parse(ReadOnlyMemory<byte>(absent))

    let absence =
        changed inspectionBody [ "observations", element [| absentDocument.RootElement.Clone() |] ]

    File.WriteAllBytes(inspectionPath, envelope algorithm inspectorKey absence)
    Expect.isNone (inspect ()) "File-not-found alone cannot certify deletion."

let private registerInventoryKeys runtime ownerPrincipal witness connection =
    let custodianOne = human "inventory-registry-custodian"
    let custodianTwo = human "inventory-inspection-custodian"
    let custodianThree = human "inventory-copy-custodian"
    grantCustodian runtime ownerPrincipal custodianOne
    grantCustodian runtime ownerPrincipal custodianTwo
    grantCustodian runtime ownerPrincipal custodianThree

    let registryKey, algorithm, registryKeyId, _ =
        registeredSigner
            runtime
            ownerPrincipal
            custodianOne
            CopySignerPurpose.LocationRegistry
            witness
            connection

    let inspectorKey, _, inspectorKeyId, _ =
        registeredSigner
            runtime
            ownerPrincipal
            custodianTwo
            CopySignerPurpose.LocationInspector
            witness
            connection

    let copyKey, _, copyKeyId, _ =
        registeredSigner
            runtime
            ownerPrincipal
            custodianThree
            CopySignerPurpose.CopyAttestor
            witness
            connection

    registryKey, inspectorKey, copyKey, algorithm, registryKeyId, inspectorKeyId, copyKeyId

let private verifyKnownCopy owner app writer witness =
    let ownerPrincipal = human "inventory-owner"
    provision owner witness ownerPrincipal |> applied
    use runtime = openRuntime app writer
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let registryKey, inspectorKey, copyKey, algorithm, registryKeyId, inspectorKeyId, copyKeyId =
        registerInventoryKeys runtime ownerPrincipal witness connection

    use registryKey = registryKey
    use inspectorKey = inspectorKey
    use copyKey = copyKey
    let directory = privateRoot ()

    try
        let copyId, custodianId, copyPath, ciphertext, keyPath, tip =
            registerCopy owner connection witness copyKey algorithm copyKeyId directory

        let now = DateTimeOffset.UtcNow

        let registry =
            registryBody tip registryKeyId copyId custodianId copyPath ciphertext now

        let inspection =
            inspectionBody tip inspectorKeyId (digest registry) copyId ciphertext now

        let registryEnvelope = envelope algorithm registryKey registry
        let inspectionEnvelope = envelope algorithm inspectorKey inspection
        let registryPath = privateBytes directory "registry.json" registryEnvelope
        let inspectionPath = privateBytes directory "inspection.json" inspectionEnvelope
        let caseId = Guid.NewGuid()

        withEnvironment registryPath inspectionPath keyPath (fun () ->
            let result = seal connection witness caseId tip.TipSequence tip.TipHash
            Expect.isSome result "Exact known-copy set permits only a live-purge inventory seal."
            Expect.equal result.Value.CopyCount 1L "One known retained copy remains pending."

            assertRefusals
                connection
                witness
                caseId
                tip
                algorithm
                registryKey
                inspectorKey
                registry
                inspection
                (observation copyId ciphertext)
                registryPath
                inspectionPath
                registryEnvelope
                inspectionEnvelope)
    finally
        Directory.Delete(directory, true)

let tests =
    testList
        "managed-copy inventory"
        [
            testCase
                "[CC-BACKUP-001] independently signed exact known-copy inventory seals live purge but not deletion"
                (fun _ -> withAuthorityRuntimeDatabase verifyKnownCopy)
        ]
