module internal ClaimCore.IntegrationTests.ManagedCopyInventoryFixture

open System
open System.Collections.Generic
open System.Data
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open Npgsql
open NSec.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Database
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.Hosting
open ClaimCore.Application

let digest (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexStringLower

let stamp (value: DateTimeOffset) =
    value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

let element value =
    JsonSerializer.SerializeToElement(value)

let nil =
    use document = JsonDocument.Parse("null")
    document.RootElement.Clone()

let canonical (fields: (string * JsonElement) list) =
    let sorted = SortedDictionary<string, JsonElement>(StringComparer.Ordinal)

    for name, value in fields do
        sorted.Add(name, value)

    Encoding.ASCII.GetBytes(JsonSerializer.Serialize(sorted) + "\n")

let envelope (algorithm: SignatureAlgorithm) (key: Key) (body: byte array) =
    let signature = algorithm.Sign(key, body)
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(body))

    canonical
        [
            "report", element (document.RootElement.Clone())
            "signatureBase64", element (Convert.ToBase64String(signature))
        ]

let changed (source: byte array) (replacements: (string * JsonElement) list) =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(source))
    let fields = SortedDictionary<string, JsonElement>(StringComparer.Ordinal)

    for property in document.RootElement.EnumerateObject() do
        fields[property.Name] <- element (property.Value.Clone())

    for name, value in replacements do
        fields[name] <- value

    Encoding.ASCII.GetBytes(JsonSerializer.Serialize(fields) + "\n")

let privateRoot () =
    let raw = Path.GetTempPath()

    let root =
        if OperatingSystem.IsMacOS() && raw.StartsWith("/var/", StringComparison.Ordinal) then
            "/private" + raw
        else
            raw

    let path =
        Path.Combine(root, "claimcore-copy-inventory-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(
        path,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    path

let privateBytes root name (bytes: byte array) =
    let path = Path.Combine(root, name)
    File.WriteAllBytes(path, bytes)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    path

let withEnvironment registry inspection key action =
    let names =
        [|
            "CLAIMCORE_COPY_LOCATION_REGISTRY_FILE"
            "CLAIMCORE_COPY_LOCATION_INSPECTION_FILE"
            "CLAIMCORE_COPY_COMMITMENT_KEY_FILE"
        |]

    let prior = names |> Array.map Environment.GetEnvironmentVariable

    try
        [| registry; inspection; key |]
        |> Array.iteri (fun index value -> Environment.SetEnvironmentVariable(names[index], value))

        action ()
    finally
        prior
        |> Array.iteri (fun index value -> Environment.SetEnvironmentVariable(names[index], value))

let seal
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    caseId
    cutoffSequence
    cutoffHash
    =
    use owner =
        DatabaseManagedCopyInventory.TryLoadFromPrivateConfiguration()
        |> Option.defaultWith (fun () ->
            invalidOp "Private copy inventory configuration did not load.")

    use transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead)

    (owner :> IManagedCopyErasureClearance)
        .RequireCompleteInventory(
            connection,
            transaction,
            witness,
            caseId,
            cutoffSequence,
            cutoffHash,
            CancellationToken.None
        )
    |> await

let registerCopyUntil
    owner
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (key: Key)
    (algorithm: SignatureAlgorithm)
    keyId
    directory
    (retainUntil: DateTimeOffset option)
    =
    let commitment = RandomNumberGenerator.GetBytes(32)
    let keyPath = privateBytes directory "copy-commitment.key" commitment
    let ciphertext = Array.create 128 0x41uy
    let copyPath = privateBytes directory "synthetic-copy.age" ciphertext
    let custodianId = "synthetic-backup-custodian"

    let commitmentFor label value =
        HMACSHA256.HashData(commitment, Encoding.UTF8.GetBytes(label + "\000" + value))
        |> Convert.ToHexStringLower

    let copyId = Guid.NewGuid()
    let eventId = Guid.NewGuid()

    let registration =
        registerBase owner (witness.Snapshot()) keyId eventId copyId
        |> fun source ->
            changed
                source
                ([
                    "ciphertextSha256", element (digest ciphertext)
                    "ciphertextBytes", element ciphertext.Length
                    "custodianCommitment", element (commitmentFor "custodian" custodianId)
                    "locationCommitment", element (commitmentFor "location" copyPath)
                 ]
                 @ (retainUntil
                    |> Option.map (fun instant -> [ "retainUntil", element (stamp instant) ])
                    |> Option.defaultValue []))

    let signature = algorithm.Sign(key, registration)

    ManagedCopyAdministration.ingest connection witness registration signature
    |> await
    |> acceptedCopy eventId

    copyId, custodianId, copyPath, ciphertext, keyPath, witness.Snapshot(), registration, signature

let registerCopy owner connection witness key algorithm keyId directory =
    let copyId, custodianId, copyPath, ciphertext, keyPath, tip, _, _ =
        registerCopyUntil owner connection witness key algorithm keyId directory None

    copyId, custodianId, copyPath, ciphertext, keyPath, tip

let registerInventorySigners
    (runtime: Runtime)
    ownerPrincipal
    custodianOne
    custodianTwo
    witness
    connection
    =
    grantCustodian runtime ownerPrincipal custodianOne
    grantCustodian runtime ownerPrincipal custodianTwo

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

    registryKey, inspectorKey, algorithm, registryKeyId, inspectorKeyId
