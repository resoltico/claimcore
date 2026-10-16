module ClaimCore.Tests.ContractSnapshotTests

open ClaimCore.ClientProtocol

open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Contracts
open ClaimCore.ContractGeneration

let private bytes (value: string) = Encoding.UTF8.GetBytes value

let private command =
    "dotnet run --project eng/ClaimCore.ContractGenerator -- --output contracts"

let private expected () =
    ContractArtifacts.all ()
    |> List.map (fun value -> value.Name, value.Bytes)
    |> Map.ofList

let private compare expected actual =
    let names values = values |> Map.keys |> Set.ofSeq
    let inventory = Set.union (names expected) (names actual)

    inventory
    |> Set.toList
    |> List.filter (fun name -> Map.tryFind name expected <> Map.tryFind name actual)

let private freshness () =
    let directory =
        Path.Combine(ClaimCore.TestSupport.RepositoryRoot.find (), "contracts")

    let entries = Directory.GetFileSystemEntries directory

    Expect.isTrue
        (entries |> Array.forall File.Exists)
        "Contract inventory contains regular files only"

    let actual =
        entries
        |> Array.map (fun path -> Path.GetFileName path |> nonNull, File.ReadAllBytes path)
        |> Map.ofArray

    Expect.isEmpty (compare (expected ()) actual) ("Regenerate and review: " + command)

let private negativeControls () =
    let original = expected ()
    let name = original |> Map.keys |> Seq.head

    for changed in
        [
            Map.remove name original
            Map.add "validators.stale.mjs" (bytes "stale") original
            Map.add name (bytes "changed") original
        ] do
        Expect.isNonEmpty (compare original changed) "Missing, extra and changed files refuse"

let private independentIdentity () =
    let inputs =
        [ "a.json", bytes "{\"z\":1,\"a\":\"🙂\"}"; "b.json", bytes "[true,null]" ]

    Expect.equal
        (ContractIdentity.preimage inputs |> Encoding.UTF8.GetString)
        "{\"inputs\":[{\"name\":\"a.json\",\"value\":{\"a\":\"🙂\",\"z\":1}},{\"name\":\"b.json\",\"value\":[true,null]}],\"purpose\":\"claimcore/wire-contract\"}"
        "Independent canonical frame"

    Expect.equal
        (ContractIdentity.fingerprint inputs)
        "a1fc39291ed809c551ed29df655982b6395f120b1eafb3b2e676f928958e2923"
        "Python hashlib oracle"

    Expect.throws
        (fun () -> ContractIdentity.preimage (inputs @ [ List.head inputs ]) |> ignore)
        "Duplicate identity names refuse"

let private identitySensitivity () =
    let inputs = EndpointCatalog.identityInputs ()
    let original = ContractIdentity.fingerprint inputs

    Expect.equal
        original
        (WireContractFingerprint.value EndpointCatalog.fingerprint)
        "Single identity"

    let names = inputs |> List.map fst
    Expect.equal names.Length 31 "Metadata, descriptors, default English, schemas and strict-byte/delivery profiles"

    Expect.isFalse
        (names
         |> List.exists (fun name ->
             name.EndsWith(".ts", StringComparison.Ordinal)
             || name = "endpoint-corpus.json"
             || name = "catalog.json"))
        "Derived outputs and self excluded"

    for name, _ in inputs do
        let changed =
            inputs
            |> List.map (fun (candidate, value) ->
                if candidate = name then
                    candidate, bytes "null"
                else
                    candidate, value)

        Expect.notEqual
            (ContractIdentity.fingerprint changed)
            original
            ("Identity includes " + name)

    Expect.notEqual
        (ContractIdentity.fingerprint (List.rev inputs))
        original
        "Framed inventory order matters"

let private safePublication () =
    let directory = Directory.CreateTempSubdirectory("claimcore-contract-publication-")
    let output = Path.Combine(directory.FullName, "contracts")
    let artifact name content = { Name = name; Bytes = bytes content }

    let publish values =
        values
        |> ArtifactPublication.withInventory
        |> ArtifactPublication.publish output

    try
        publish [ artifact "obsolete.json" "old" ]
        publish [ artifact "current.json" "current" ]

        Expect.isFalse
            (File.Exists(Path.Combine(output, "obsolete.json")))
            "Owned obsolete output removed"

        Expect.equal
            (File.ReadAllText(Path.Combine(output, "current.json")))
            "current"
            "New output complete"

        File.WriteAllText(Path.Combine(output, "user.txt"), "preserve")

        Expect.throws
            (fun () -> publish [ artifact "replacement.json" "replacement" ])
            "Unexpected user content refuses replacement"

        Expect.equal
            (File.ReadAllText(Path.Combine(output, "current.json")))
            "current"
            "Previous set retained"

        Expect.equal
            (File.ReadAllText(Path.Combine(output, "user.txt")))
            "preserve"
            "User content retained"
    finally
        directory.Delete true

let private repeatedGeneration () =
    let first = expected ()
    Expect.equal (expected ()) first "All generated bytes are deterministic"

    Expect.throws
        (fun () ->
            ArtifactPublication.withInventory [ { Name = "../escape"; Bytes = [||] } ]
            |> ignore)
        "Artifact names cannot escape their directory"

[<Tests>]
let tests =
    testList
        "tracked contract artifacts"
        [
            testCase "atomic publication removes only owned obsolete files" safePublication
            testCase
                "generation is deterministic and names stay within the output"
                repeatedGeneration
            testCase "tracked inventory and bytes equal in-memory generation" freshness
            testCase "freshness rejects missing extra and stale bytes" negativeControls
            testCase
                "fingerprint matches an independent canonical frame and digest"
                independentIdentity
            testCase
                "fingerprint covers every nonderived input without self reference"
                identitySensitivity
        ]
