module internal ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerRefusals

open System
open System.IO
open System.Security.Cryptography
open Expecto
open NSec.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProcessDocuments
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup

let private wrongEvent
    (witness: WitnessProtocol)
    before
    privateRoot
    (transition: byte array)
    (algorithm: SignatureAlgorithm)
    (copyKey: Key)
    inputs
    =
    let altered =
        changed transition [ "eventId", element (Guid.NewGuid().ToString("D")) ]

    let path = privateBytes privateRoot "wrong-event.json" altered

    let signature =
        privateBytes privateRoot "wrong-event.sig" (algorithm.Sign(copyKey, altered))

    let code, _ = invoke inputs path signature
    Expect.notEqual code 0 "Valid proof cannot verify a different copy event."
    Expect.equal (witness.Snapshot().TipSequence) before "Wrong event leaves witness tip unchanged."

let private wrongLocation
    (witness: WitnessProtocol)
    before
    privateRoot
    (copy: PhysicalCopyDescriptor)
    copyId
    proofPath
    signaturePath
    inputs
    attestationPath
    attestationSignaturePath
    =
    let wrongKey = RandomNumberGenerator.GetBytes(32)

    try
        let wrongKeyPath = privateBytes privateRoot "wrong-location.key" wrongKey

        let config =
            ownerInput
                copyId
                copy.CiphertextPath
                proofPath
                signaturePath
                wrongKeyPath
                (FileInfo(copy.CiphertextPath).Length)

        let path = privateBytes privateRoot "wrong-physical-input.json" config

        let wrongFiles =
            inputs
            |> List.map (fun (name, value) ->
                if name = "CLAIMCORE_COPY_PHYSICAL_INPUT_FILE" then
                    name, path
                else
                    name, value)

        let code, _ = invoke wrongFiles attestationPath attestationSignaturePath
        Expect.notEqual code 0 "Valid proof cannot bypass owner HMAC location custody."

        Expect.equal
            (witness.Snapshot().TipSequence)
            before
            "Wrong location key leaves witness tip unchanged."
    finally
        CryptographicOperations.ZeroMemory(wrongKey)

let exercise
    witness
    before
    privateRoot
    transition
    algorithm
    copyKey
    copy
    copyId
    proofPath
    signaturePath
    inputs
    attestationPath
    attestationSignaturePath
    =
    wrongEvent witness before privateRoot transition algorithm copyKey inputs

    wrongLocation
        witness
        before
        privateRoot
        copy
        copyId
        proofPath
        signaturePath
        inputs
        attestationPath
        attestationSignaturePath
