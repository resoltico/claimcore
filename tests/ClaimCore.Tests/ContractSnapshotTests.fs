module ClaimCore.Tests.ContractSnapshotTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
open ClaimCore.ContractGeneration

let private bytes (value: string) = Encoding.UTF8.GetBytes value

let private expected () =
    ContractArtifacts.all ()
    |> List.map (fun artifact -> artifact.Name, artifact.Bytes)
    |> Map.ofList

let private differences expected actual =
    Set.union (expected |> Map.keys |> Set.ofSeq) (actual |> Map.keys |> Set.ofSeq)
    |> Set.filter (fun name -> Map.tryFind name expected <> Map.tryFind name actual)

let private freshness () =
    let directory =
        Path.Combine(ClaimCore.TestSupport.RepositoryRoot.find (), "contracts")

    let entries = Directory.GetFileSystemEntries directory
    Expect.isTrue (entries |> Array.forall File.Exists) "Only regular artifacts are delivered"

    let actual =
        entries
        |> Array.map (fun path -> Path.GetFileName path |> nonNull, File.ReadAllBytes path)
        |> Map.ofArray

    Expect.isEmpty
        (differences (expected ()) actual)
        "Regenerate: dotnet run --project eng/ClaimCore.ContractGenerator -- --output contracts"

    Expect.isNonEmpty actual "The complete produced inventory is delivered"

let private controls () =
    let original = expected ()
    let name = original |> Map.keys |> Seq.head

    for changed in
        [
            Map.remove name original
            Map.add "stale.json" (bytes "stale") original
            Map.add name (bytes "changed") original
        ] do
        Expect.isNonEmpty
            (differences original changed)
            "Missing extra and changed bytes are detected"

let private fingerprint () =
    let emitted = expected ()
    let manifest = Map.find "contract-manifest.json" emitted
    let hash = SHA256.HashData manifest |> Convert.ToHexStringLower

    Expect.equal
        ConsumerContract.identity
        hash
        "Runtime identity hashes the canonical consumer manifest"

    Expect.stringContains
        (Map.find "client.ts" emitted |> Encoding.UTF8.GetString)
        hash
        "Browser binds the same consumer identity"

    Expect.equal (expected ()) emitted "Regeneration is byte identical"

let private identityInputs () =
    let artifacts = ContractArtifacts.all ()

    let manifest =
        artifacts
        |> List.find (fun artifact -> artifact.Name = "contract-manifest.json")

    use document =
        System.Text.Json.JsonDocument.Parse(System.ReadOnlyMemory<byte> manifest.Bytes)

    let names =
        document.RootElement.GetProperty("inputs").EnumerateArray()
        |> Seq.map (fun row -> row.GetProperty("name").GetString() |> nonNull)
        |> Set.ofSeq

    let inputs =
        artifacts
        |> List.filter (fun artifact -> artifact.Role = ArtifactRole.RuntimePrecursor)

    Expect.equal
        (inputs |> List.map _.Name |> Set.ofList)
        names
        "Manifest membership follows the produced roles"

    inputs

let private qualificationIdentity () =
    let all = ContractArtifacts.all ()

    for artifact in all |> List.filter (fun item -> item.Role = ArtifactRole.QualificationOrTooling) do
        let changed =
            all
            |> List.map (fun item ->
                if item.Name = artifact.Name then
                    { item with
                        Bytes = Array.append item.Bytes [| 32uy |]
                    }
                else
                    item)

        Expect.equal
            (ContractIdentity.manifest changed |> ContractIdentity.digest)
            ConsumerContract.identity
            "Qualification bytes do not change runtime identity"


let private consumerIdentity () =
    let inputs = identityInputs ()

    for input in inputs do
        let name = input.Name

        let changed =
            inputs
            |> List.map (fun artifact ->
                if artifact.Name = name then
                    { artifact with
                        Bytes = Array.append artifact.Bytes [| 32uy |]
                    }
                else
                    artifact)

        Expect.notEqual
            (ContractIdentity.manifest changed |> ContractIdentity.digest)
            ConsumerContract.identity
            (name + " changes consumer identity")

    let forward = ContractIdentity.manifest inputs

    Expect.equal
        (ContractIdentity.manifest (List.rev inputs))
        forward
        "Manifest ordering is ordinal and independent of enumeration"

    Expect.notEqual
        (ContractIdentity.manifest (List.tail inputs))
        forward
        "Missing inputs change identity"

    Expect.throws
        (fun () -> ContractIdentity.manifest (inputs @ [ List.head inputs ]) |> ignore)
        "Duplicate identity inputs refuse"

    let vector =
        ContractIdentity.manifest
            [
                {
                    Name = "a.json"
                    Bytes = bytes "abc"
                    Role = ArtifactRole.RuntimePrecursor
                }
            ]
        |> Encoding.UTF8.GetString

    Expect.equal
        vector
        "{\"format\":1,\"inputs\":[{\"name\":\"a.json\",\"sha256\":\"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\"}]}\n"
        "Independent canonical manifest vector"

let private safePublication () =
    let directory = Directory.CreateTempSubdirectory("claimcore-contract-publication-")
    let output = Path.Combine(directory.FullName, "contracts")

    let artifact content =
        {
            Name = "current.json"
            Role = ArtifactRole.RuntimePrecursor
            Bytes = bytes content
        }

    try
        ArtifactPublication.publish output [ artifact "first" ]
        ArtifactPublication.publish output [ artifact "second" ]

        Expect.equal
            (File.ReadAllText(Path.Combine(output, "current.json")))
            "second"
            "Owned bytes atomically replaced"

        File.WriteAllText(Path.Combine(output, "user.txt"), "preserve")

        Expect.throws
            (fun () -> ArtifactPublication.publish output [ artifact "third" ])
            "Unexpected content refuses"

        Expect.equal
            (File.ReadAllText(Path.Combine(output, "current.json")))
            "second"
            "Previous set survives refusal"

        Expect.equal
            (File.ReadAllText(Path.Combine(output, "user.txt")))
            "preserve"
            "Unowned content survives refusal"
    finally
        directory.Delete true

let private producedNames () =
    let directory = Directory.CreateTempSubdirectory("claimcore-contract-names-")
    let output = Path.Combine(directory.FullName, "contracts")

    try
        Expect.throws
            (fun () ->
                ArtifactPublication.publish
                    output
                    [
                        {
                            Name = "../escape"
                            Bytes = [||]
                            Role = ArtifactRole.RuntimePrecursor
                        }
                    ])
            "Names cannot escape"

        let repeated =
            {
                Name = "a.json"
                Bytes = [||]
                Role = ArtifactRole.RuntimePrecursor
            }

        Expect.throws
            (fun () -> ArtifactPublication.publish output [ repeated; repeated ])
            "Duplicate names refuse"

        Expect.isFalse (Directory.Exists output) "Rejected output never created"
    finally
        directory.Delete true

let private computedVectors
    name
    (generate: byte array -> byte array)
    (groups: (string * string) list)
    =
    let path =
        Path.Combine(ClaimCore.TestSupport.RepositoryRoot.find (), "tests/fixtures", name)

    let original = JsonNode.Parse(File.ReadAllBytes path) |> nonNull
    let poisoned = original.DeepClone()

    for group, result in groups do
        for row in (poisoned[group] |> nonNull).AsArray() do
            (nonNull row)[result] <- JsonValue.Create "SYNTHETIC_WRONG_EXPECTATION"

    let emitted =
        generate (bytes (poisoned.ToJsonString())) |> JsonNode.Parse |> nonNull

    for group, _ in groups do
        Expect.isTrue
            (JsonNode.DeepEquals(original[group], emitted[group]))
            "Generated expected values are computed from Wire and match the independent oracle."

let private policyVectors () =
    computedVectors
        "delivery-vectors.json"
        PolicyVectors.delivery
        [ "knowledge", "knowledge"; "evidence", "evidence"; "accumulate", "result" ]

    computedVectors "correlation-vectors.json" PolicyVectors.correlation [ "cases", "correlated" ]

[<Tests>]
let tests =
    testList
        "tracked contract artifacts"
        [
            testCase
                "publication replaces owned outputs and preserves unexpected content"
                safePublication
            testCase "produced names are unique and stay within the output" producedNames
            testCase
                "published policy vectors compute results instead of copying oracle answers"
                policyVectors
            testCase "tracked exact inventory and bytes equal generation" freshness
            testCase "freshness rejects missing extra and stale bytes" controls
            testCase
                "consumer identity covers the canonical manifest and binds TypeScript"
                fingerprint
            testCase "every authoritative consumer document changes identity" consumerIdentity
            testCase
                "qualification artifacts retain freshness without changing runtime identity"
                qualificationIdentity
        ]
