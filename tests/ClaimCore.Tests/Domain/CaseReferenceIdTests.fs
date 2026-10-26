module ClaimCore.Tests.CaseReferenceIdTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

let private installation = Guid.Parse "1f0e7c52-6b3a-4d0e-9a51-2c7d8e9f0a1b"

let private oracle () =
    let path =
        Path.Combine(RepositoryRoot.find (), "tests/fixtures/record-format-vectors.json")

    use document = JsonDocument.Parse(File.ReadAllBytes path)
    let vectors = document.RootElement.GetProperty "caseReferenceIds"

    Expect.equal
        (vectors.GetProperty("installationId").GetString() |> nonNull |> Guid.Parse)
        installation
        "Independent installation"

    let accepted = vectors.GetProperty("accepted").EnumerateArray() |> Seq.toList
    Expect.equal accepted.Length 4 "All independent accepted identifiers"

    for vector in accepted do
        let reference = vector.GetProperty("reference").GetString() |> nonNull
        let expected = vector.GetProperty("caseId").GetString() |> nonNull |> Guid.Parse
        let actual = CaseReferenceId.encode installation reference
        Expect.equal actual expected "Independent network-order UUIDv8 oracle"
        let bytes = actual.ToByteArray(bigEndian = true)
        Expect.equal (bytes[6] >>> 4) 8uy "UUID version 8"
        Expect.equal (bytes[8] >>> 6) 2uy "UUID variant 10"

    for vector in vectors.GetProperty("refused").EnumerateArray() do
        let reference = vector.GetString() |> nonNull

        Expect.throws
            (fun () -> CaseReferenceId.encode installation reference |> ignore)
            "Independent refusal vector"

let private identity () =
    let original = CaseReferenceId.encode installation "Demo-0001"

    for reference in [ "DEMO-0001"; "demo-0001"; "DeMo-0001" ] do
        Expect.equal
            (CaseReferenceId.encode installation reference)
            original
            "Resource identity folds ASCII case"

    for otherInstallation, reference in
        [
            Guid.Parse "90000000-0000-4000-8000-000000000002", "Demo-0001"
            installation, "Demo-0002"
            installation, "Demo-0001!"
        ] do
        Expect.notEqual
            (CaseReferenceId.encode otherInstallation reference)
            original
            "Installation and reference key bind identity"

let private refusals () =
    Expect.throws
        (fun () -> CaseReferenceId.encode Guid.Empty "reference" |> ignore)
        "Installation required"

    for reference in
        [
            Unchecked.defaultof<string>
            ""
            "a b"
            "réference"
            "réference"
            String([| char 0xD800 |])
        ] do
        Expect.throws
            (fun () -> CaseReferenceId.encode installation reference |> ignore)
            "Permanent reference profile"

[<Tests>]
let tests =
    testList
        "case reference identity"
        [
            testCase "independent SHA-256 reference vectors match UUIDv8 identifiers" oracle
            testCase "installation and folded ASCII reference bind the identifier" identity
            testCase "empty identities and references outside the profile refuse" refusals
        ]
