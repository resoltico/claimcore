module internal ClaimCore.IntegrationTests.CaseWitnessPayloadPruneRotation

open System.Threading
open System
open System.Security.Cryptography
open Expecto
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures

let private applyRotation
    (file: WriterCapabilityFile)
    witnessOwner
    identity
    rotationId
    oldKeyId
    newKeyId
    check
    envelope
    =
    file.Use(fun material ->
        KeyRotation.rotateKey
            witnessOwner
            identity
            rotationId
            oldKeyId
            newKeyId
            check
            envelope
            material
        |> ignore)

let withRotatedWitness witnessOwner writer (witness: WitnessProtocol) action =
    let oldKeyId, _ =
        (witness.EvidenceStore.ReadKeyCheck(CancellationToken.None).GetAwaiter().GetResult())

    let oldKey = witnessKey ()
    let newKey = RandomNumberGenerator.GetBytes(32)

    try
        let newKeyId = Guid.NewGuid()

        let custody =
            new KeyRing(newKeyId, [ oldKeyId, oldKey; newKeyId, newKey ]) :> IKeyCustody

        let rotationId = Guid.NewGuid()
        let identity = witness.Identity
        let check = KeyCheck.create custody identity.InstallationId identity.LineageId

        let envelope =
            KeyCheck.rotationEnvelope
                custody
                identity.InstallationId
                identity.LineageId
                identity.Epoch
                rotationId
                oldKeyId
                newKeyId

        let path =
            Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failtest "Isolated writer capability path is absent")

        use file = WriterCapabilityFile.Load(path)
        applyRotation file witnessOwner identity rotationId oldKeyId newKeyId check envelope
        let store = file.Use(fun material -> new Store(writer, identity, material))
        use rotated = new WitnessProtocol(store, custody, identity)
        rotated.Admit(CancellationToken.None).GetAwaiter().GetResult()
        action rotated
    finally
        CryptographicOperations.ZeroMemory(oldKey)
        CryptographicOperations.ZeroMemory(newKey)
