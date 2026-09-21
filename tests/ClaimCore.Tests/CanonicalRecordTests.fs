module ClaimCore.Tests.CanonicalRecordTests

open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.Tests.Fixtures

let private vectorPath =
    Path.Combine(RepositoryRoot.find (), "tests/fixtures/canonical-record-vectors.json")

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
            ((restored |> Claim.view |> CaseRecord.encodeSnapshot |> Encoding.UTF8.GetString) =
                canonical)
            "Stable retained record"

        let withExtraField =
            canonical.Replace(
                "\"status\":\"OPENED\"",
                "\"unexpected\":\"x\",\"status\":\"OPENED\""
            )

        Expect.isError
            (withExtraField |> Encoding.UTF8.GetBytes |> CaseRecord.decodeSnapshot)
            "Retained snapshots reject an added claims field")

let private correctionRecord =
    testCase
        "CORRECT_CASE uses explicit complete groups without changing historical format-2 vectors"
        (fun () ->
            let request =
                {
                    OperationId = System.Guid.Parse("20000000-0000-4000-8000-000000000009")
                    CaseReference = "UNIT-001"
                    ExpectedVersion = 3L
                    Command =
                        Command.CorrectCase
                            {
                                Registration = RegistrationCorrection.Keep
                                Decision =
                                    DecisionCorrection.Replace
                                        {
                                            PaymentDecisionDate = "2026-08-15"
                                            PayableAmount = "650.00"
                                            PayableCurrency = "EUR"
                                        }
                                Payment = PaymentCorrection.Replace "2026-08-21"
                            }
                }

            let expected =
                "{\"protocolVersion\":2,\"operationId\":\"20000000-0000-4000-8000-000000000009\",\"caseReference\":\"UNIT-001\",\"expectedVersion\":3,\"command\":{\"type\":\"CORRECT_CASE\",\"registration\":{\"mode\":\"KEEP\"},\"decision\":{\"mode\":\"REPLACE\",\"decision\":{\"paymentDecisionDate\":\"2026-08-15\",\"payableAmount\":\"650.00\",\"payableCurrency\":\"EUR\"}},\"payment\":{\"mode\":\"REPLACE\",\"paymentDate\":\"2026-08-21\"}}}"

            let actual = RequestRecord.encode request |> Encoding.UTF8.GetString
            Expect.equal actual expected "Deterministic grouped command bytes"

            Expect.equal
                (RequestRecord.decode 65536 (Encoding.UTF8.GetBytes expected))
                (Ok request)
                "Round trip"

            let incomplete =
                expected.Replace(
                    "\"payment\":{\"mode\":\"REPLACE\",\"paymentDate\":\"2026-08-21\"}",
                    ""
                )

            Expect.isError
                (RequestRecord.decode 65536 (Encoding.UTF8.GetBytes incomplete))
                "Every group is required")

let tests =
    testList
        "stable identity and historical encoding"
        [ commandVectors; correctionRecord; snapshotVector ]
