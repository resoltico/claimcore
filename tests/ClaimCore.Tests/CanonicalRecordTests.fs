module ClaimCore.Tests.CanonicalRecordTests

open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private vectorPath =
    Path.GetFullPath(
        Path.Combine(__SOURCE_DIRECTORY__, "../fixtures/canonical-record-vectors.json")
    )

let private text (value: JsonElement) (name: string) =
    value.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Golden vector text is missing")

let private commandVectors =
    testCase "all eight command encodings and identities match fixed protocol-2 vectors" (fun () ->
        use document = JsonDocument.Parse(File.ReadAllText(vectorPath))

        let vectors =
            document.RootElement.GetProperty("requests").EnumerateArray() |> Seq.toList

        Expect.equal vectors.Length 8 "Every command family"

        for vector in vectors do
            let canonical = text vector "canonicalUtf8"

            let request =
                canonical
                |> Encoding.UTF8.GetBytes
                |> RequestRecord.decode SemanticContract.current.RequestByteLimit
                |> accepted

            let actual = request |> RequestRecord.encode |> Encoding.UTF8.GetString

            Expect.isTrue
                (actual = canonical)
                "No identity format change hidden in a layer refactor"

            let fingerprint = request |> Operation.prepare |> accepted |> Operation.fingerprint
            Expect.equal fingerprint (text vector "sha256") "Fixed independently computed identity")

let private snapshotVector =
    testCase "snapshot-2 encoding stays independent of advisory rendering metadata" (fun () ->
        use document = JsonDocument.Parse(File.ReadAllText(vectorPath))
        let canonical = text document.RootElement "snapshot"

        let view =
            canonical |> Encoding.UTF8.GetBytes |> CaseRecord.decodeSnapshot |> accepted

        let restored = view |> Claim.restore |> accepted

        Expect.isTrue
            ((restored |> Claim.view |> CaseRecord.encodeSnapshot |> Encoding.UTF8.GetString) = canonical)
            "Stable retained record"

        let withExtraField =
            canonical.Replace(
                "\"status\":\"OPENED\"",
                "\"unexpected\":\"x\",\"status\":\"OPENED\""
            )

        Expect.isError
            (withExtraField |> Encoding.UTF8.GetBytes |> CaseRecord.decodeSnapshot)
            "Retained snapshots reject an added claims field")

let tests =
    testList "stable identity and historical encoding" [ commandVectors; snapshotVector ]
