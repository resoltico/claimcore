module ClaimCore.Tests.CanonicalRecordTests

open ClaimCore.Contract

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport

let private withVectors action =
    let path =
        Path.Combine(RepositoryRoot.find (), "tests/fixtures/record-format-vectors.json")

    use document = JsonDocument.Parse(File.ReadAllBytes path)
    action document.RootElement

let private text (element: JsonElement) (name: string) =
    element.GetProperty(name).GetString() |> nonNull

let private bytes element =
    text element "canonicalHex" |> Convert.FromHexString

let private requests () =
    withVectors (fun root ->
        let vectors = root.GetProperty("caseRequests").EnumerateArray() |> Seq.toList
        Expect.equal vectors.Length 4 "All independent request vectors"

        for vector in vectors do
            let encoded = bytes vector
            let request = RequestRecord.decode 65536 encoded |> Fixtures.accepted

            Expect.isTrue
                (RequestRecord.encode request = encoded)
                "Independent canonical request bytes"

            Expect.equal
                (Convert.ToHexStringLower(LedgerEntry.requestDigest encoded))
                (text vector "sha256")
                "Independent digest"


        let first = RequestRecord.decode 65536 (bytes vectors.Head) |> Fixtures.accepted
        Expect.equal first.CaseReference "Demo-0001" "Independent authored target"
        Expect.equal first.ExpectedRevision 0L "Independent initial revision"

        match first.Act with
        | (CaseAct.Open registration) ->
            Expect.equal
                registration.ClaimantName
                "Rīgas Tulkojumu Birojs SIA"
                "Independent Latvian text"

            Expect.equal registration.InsurerName "شركة التأمين المثال" "Independent Arabic text"
        | _ -> failtest "Expected the independent OPEN meaning")

let private snapshot () =
    withVectors (fun root ->
        let vector = root.GetProperty "snapshot"
        let encoded = bytes vector
        let observed = CaseRecord.decodeSnapshot encoded |> Fixtures.accepted
        Expect.equal observed.Reference "Demo-0001" "Independent reference"
        Expect.equal observed.Revision 1L "Independent revision"

        match observed.View with
        | CaseView.Live(fields, Disposition.Active) ->
            Expect.equal fields.ClaimedAmount "1000" "Canonical monetary spelling"
            Expect.equal fields.IncidentCountry "LV" "Country syntax"
            Expect.isNone fields.PaymentDecisionDate "Independent undecided state"
        | _ -> failtest "Expected ACTIVE live snapshot"

        Expect.isTrue (CaseRecord.encodeSnapshot observed = encoded) "Independent snapshot bytes"

        let extra =
            text vector "canonicalUtf8"
            |> fun value ->
                value.Replace("\"status\":\"OPENED\"", "\"status\":\"OPENED\",\"unexpected\":true")

        Expect.isError
            (CaseRecord.decodeSnapshot (Encoding.UTF8.GetBytes extra))
            "No extra business fields")

let private authority () =
    withVectors (fun root ->
        let vector = root.GetProperty "authorityRecord"
        let encoded = bytes vector
        let request = AuthorityRecord.decode 65536 encoded |> Fixtures.accepted

        Expect.equal
            request.OperationId
            (Guid.Parse "20000000-0000-4000-8000-000000000002")
            "Independent operation"

        Expect.equal request.Target.Subject "handler-1" "Independent target principal"

        Expect.equal
            request.Act
            (RecordedAuthorityAct.SetGrant(
                "CASE_HANDLER",
                RecordedGrantScope.CaseReference "Demo-0001",
                true
            ))
            "Independent grant meaning"

        Expect.isTrue (AuthorityRecord.encode request = encoded) "Independent authority bytes"

    )

let private formats () =
    withVectors (fun root ->
        let cases =
            root.GetProperty("caseRequests")[0] |> fun item -> text item "canonicalUtf8"

        let snapshot = root.GetProperty "snapshot" |> fun item -> text item "canonicalUtf8"

        let authority =
            root.GetProperty "authorityRecord" |> fun item -> text item "canonicalUtf8"

        for format in [ "0"; "1"; "99"; "5.0"; "5e0"; "\"5\"" ] do
            Expect.isError
                (RequestRecord.decode
                    65536
                    (Encoding.UTF8.GetBytes(cases.Replace("\"format\":5", "\"format\":" + format))))
                "Unknown or noninteger format refuses"

        Expect.isError
            (CaseRecord.decodeSnapshot (
                Encoding.UTF8.GetBytes(snapshot.Replace("\"format\":4", "\"format\":2"))
            ))
            "No old snapshot reinterpretation"

        Expect.isError
            (AuthorityRecord.decode
                65536
                (Encoding.UTF8.GetBytes(authority.Replace("\"format\":2", "\"format\":1"))))
            "No old authority reinterpretation")

[<Tests>]
let tests =
    testList
        "canonical durable records"
        [
            testCase "format-5 requests reproduce independent bytes and meaning" requests
            testCase "format-4 snapshots reproduce independent bytes and meaning" snapshot
            testCase "format-2 authority requests reproduce independent bytes and meaning" authority
            testCase "unknown formats and noninteger format lexemes refuse" formats
        ]
