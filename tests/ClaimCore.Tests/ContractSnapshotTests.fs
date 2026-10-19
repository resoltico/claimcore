module ClaimCore.Tests.ContractSnapshotTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Wire
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

    Expect.equal (actual.Count) 8 "Seven contract artifacts and the package marker"

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
    let openapi = Map.find "openapi.json" emitted
    let hash = SHA256.HashData openapi |> Convert.ToHexStringLower
    Expect.equal Fingerprint.value hash "Fingerprint is SHA-256 of emitted OpenAPI bytes"

    Expect.stringContains
        (Map.find "client.ts" emitted |> Encoding.UTF8.GetString)
        hash
        "Browser binds the same fingerprint"

    let root = JsonNode.Parse(openapi) |> nonNull
    root["info"] <- JsonNode.Parse("""{"title":"ClaimCore","version":"changed"}""")

    Expect.notEqual
        (SHA256.HashData(bytes (root.ToJsonString())) |> Convert.ToHexStringLower)
        hash
        "Contract byte changes alter identity"

let private presentationIndependence () =
    let original = Fingerprint.value
    let descriptors = JsonNode.Parse(ContractDocuments.descriptors ()) |> nonNull
    let firstField = (descriptors["fields"] |> nonNull).AsArray()[0] |> nonNull
    firstField["label"] <- JsonValue.Create "Different English label"
    firstField["meaning"] <- JsonValue.Create "Different English meaning"

    descriptors["diagnostics"] <-
        JsonNode.Parse(
            """{"diagnostic.CORE_INTERNAL_ERROR":[{"literal":"Different presentation"}]}"""
        )

    Expect.notEqual
        (bytes (descriptors.ToJsonString()))
        (ContractDocuments.descriptors ())
        "Presentation control actually changed"

    let artifacts =
        expected () |> Map.add "descriptors.json" (bytes (descriptors.ToJsonString()))

    let changedIdentity =
        Map.find "openapi.json" artifacts |> SHA256.HashData |> Convert.ToHexStringLower

    Expect.equal
        changedIdentity
        original
        "Changing English labels and meanings leaves contract identity unchanged"

    Expect.equal
        Fingerprint.value
        changedIdentity
        "The runtime fingerprint hashes exactly the contract artifact"

    let originalArtifacts = expected ()
    Expect.equal (expected ()) originalArtifacts "Generation is deterministic"

let private safePublication () =
    let directory = Directory.CreateTempSubdirectory("claimcore-contract-publication-")
    let output = Path.Combine(directory.FullName, "contracts")

    let artifact content =
        {
            Name = "current.json"
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
            (fun () -> ArtifactPublication.publish output [ { Name = "../escape"; Bytes = [||] } ])
            "Names cannot escape"

        let repeated = { Name = "a.json"; Bytes = [||] }

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
            testCase "fingerprint covers emitted OpenAPI bytes and binds TypeScript" fingerprint
            testCase
                "presentation stays outside deterministic Wire identity"
                presentationIndependence
        ]
