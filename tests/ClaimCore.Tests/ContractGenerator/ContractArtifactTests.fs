module ClaimCore.Tests.ContractArtifactTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.ProtocolGeneration

let private bytes (value: string) = Encoding.UTF8.GetBytes value

let private expected () =
    ContractArtifacts.all ()
    |> List.map (fun artifact -> artifact.Name, artifact.Bytes)
    |> Map.ofList

let private freshness () =
    let directory =
        Path.Combine(ClaimCore.TestSupport.RepositoryRoot.find (), "contracts")

    let artifacts = ContractArtifacts.all ()

    let reference =
        Path.Combine(ClaimCore.TestSupport.RepositoryRoot.find (), "docs/cli.md")

    Expect.equal
        (File.ReadAllBytes reference)
        (CliDocumentation.source ())
        "The CLI reference is generated from the same catalog"

    Expect.isNonEmpty artifacts "The complete produced inventory is delivered"

    Expect.isTrue
        (ArtifactPublication.check directory artifacts)
        "Regenerate: dotnet run --project eng/ClaimCore.ProtocolGenerator -- --output contracts"

let private controls () =
    let temporary = Directory.CreateTempSubdirectory("claimcore-contract-control-")

    try
        let output = Path.Combine(temporary.FullName, "contracts")

        let artifact =
            {
                Name = "sample.json"
                Bytes = bytes "{}"
            }

        ArtifactPublication.publish output [ artifact ]
        Expect.isTrue (ArtifactPublication.check output [ artifact ]) "Fresh artifacts pass"
        let path = Path.Combine(output, artifact.Name)
        File.WriteAllBytes(path, bytes "{}\n")
        Expect.isFalse (ArtifactPublication.check output [ artifact ]) "A changed byte fails"

        Expect.equal
            (File.ReadAllBytes path)
            (bytes "{}\n")
            "Check does not repair the changed file"

        File.Delete path
        Expect.isFalse (ArtifactPublication.check output [ artifact ]) "A missing artifact fails"
        File.WriteAllBytes(path, artifact.Bytes)
        let extra = Path.Combine(output, "unexpected.json")
        File.WriteAllText(extra, "{}")
        Expect.isFalse (ArtifactPublication.check output [ artifact ]) "An extra artifact fails"
        Expect.isTrue (File.Exists extra) "Check preserves unexpected content"
    finally
        temporary.Delete true

let private fingerprint () =
    let emitted = expected ()

    let document =
        Map.find "openapi.json" emitted
        |> StrictJson.read System.Int32.MaxValue
        |> Result.defaultWith (fun _ -> failtest "Generated JSON")

    let hash =
        CanonicalWriter.bytes document |> SHA256.HashData |> Convert.ToHexStringLower

    Expect.equal
        ClaimCore.Protocol.ContractIdentity.value
        hash
        "Runtime identity hashes canonical published OpenAPI."

    Expect.stringContains
        (Map.find "client.ts" emitted |> Encoding.UTF8.GetString)
        hash
        "Browser binds the same structural identity."

    Expect.equal (expected ()) emitted "Regeneration is byte identical."

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

let private customOutput () =
    let directory = Directory.CreateTempSubdirectory("claimcore-contract-output-")

    try
        let output = Path.Combine(directory.FullName, "custom-contracts")

        Expect.equal
            (ClaimCore.ProtocolGenerator.Program.main [| "--output"; output |])
            0
            "Custom contract output publishes"

        Expect.equal
            (ClaimCore.ProtocolGenerator.Program.main [| "--output"; output; "--check" |])
            0
            "Custom output checks independently"

        Expect.isFalse
            (Directory.Exists(Path.Combine(directory.FullName, "docs")))
            "Custom output cannot write a sibling documentation directory"
    finally
        directory.Delete true

[<Tests>]
let tests =
    testList
        "tracked contract artifacts"
        [
            testCase
                "publication replaces owned outputs and preserves unexpected content"
                safePublication
            testCase "produced names are unique and stay within the output" producedNames
            testCase "custom output stays within the selected contract directory" customOutput
            testCase "tracked exact inventory and bytes equal generation" freshness
            testCase "freshness rejects missing extra and stale bytes" controls
            testCase "canonical OpenAPI identity binds runtime and browser" fingerprint
        ]
