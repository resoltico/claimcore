module ClaimCore.Tests.CanonicalRecordTests

open ClaimCore.Protocol
open ClaimCore.Json

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application
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
        Expect.equal first.Authored.CaseReference "Demo-0001" "Independent authored target"
        Expect.equal first.ExpectedRevision 0L "Independent initial revision"

        match first.Authored.Act with
        | (AuthoredCaseAct.Open registration) ->
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
        Expect.equal (CaseReference.format observed.Reference) "Demo-0001" "Independent reference"
        Expect.equal observed.Revision 1L "Independent revision"

        match observed.State with
        | CaseState.Active(fields) ->
            Expect.equal
                (Money.format fields.Registration.ClaimedAmount)
                "1000"
                "Canonical monetary spelling"

            Expect.equal
                (CountryCode.format fields.Registration.IncidentCountry)
                "LV"
                "Country syntax"

            Expect.isNone
                (fields.Decision |> Option.map _.PaymentDecisionDate)
                "Independent undecided state"
        | _ -> failtest "Expected ACTIVE live snapshot"

        Expect.isTrue (CaseRecord.encodeSnapshot observed = encoded) "Independent snapshot bytes"

        let variedScale =
            text vector "canonicalUtf8"
            |> fun source ->
                source.Replace("\"claimedAmount\":\"1000\"", "\"claimedAmount\":\"1000.00\"")
            |> Encoding.UTF8.GetBytes

        Expect.notEqual
            variedScale
            encoded
            "The control really changes the retained scalar spelling"

        let typed = CaseRecord.decodeSnapshot variedScale |> Fixtures.accepted
        Expect.equal typed observed "A durable scalar spelling becomes the same typed value"

        Expect.equal
            (Case.ofAccepted typed |> Result.map Case.snapshot)
            (Ok observed)
            "Restoration does not re-render and compare string fields"

        Expect.equal
            (CaseRecord.encodeSnapshot typed)
            encoded
            "Typed values emit the unchanged canonical bytes"

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
            (ClaimCore.Domain.AuthorityAct.Grant
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.CaseReference "Demo-0001"
                })
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

        Expect.equal
            [
                RecordVersions.CaseRequest
                RecordVersions.Authority
                RecordVersions.Snapshot
                RecordVersions.LedgerEntry
                RecordVersions.CaseReferenceIdentity
            ]
            [ 1; 1; 1; 1; 1 ]
            "Each retained grammar has its adopted format number"

        for format in [ "0"; "7"; "99"; "5"; "1.0"; "1e0"; "\"1\"" ] do
            Expect.isError
                (RequestRecord.decode
                    65536
                    (Encoding.UTF8.GetBytes(cases.Replace("\"format\":1", "\"format\":" + format))))
                "Unknown or noninteger format refuses"

        Expect.isError
            (CaseRecord.decodeSnapshot (
                Encoding.UTF8.GetBytes(snapshot.Replace("\"format\":1", "\"format\":2"))
            ))
            "No old snapshot reinterpretation"

        Expect.isError
            (AuthorityRecord.decode
                65536
                (Encoding.UTF8.GetBytes(authority.Replace("\"format\":1", "\"format\":4"))))
            "No old authority reinterpretation")

let private operationIdentity () =
    withVectors (fun root ->
        for vector in root.GetProperty("caseRequests").EnumerateArray() do
            let encoded = bytes vector
            let request = RequestRecord.decode 65536 encoded |> Fixtures.accepted
            let tree = StrictJson.read 65536 encoded |> Fixtures.accepted

            Expect.equal
                (JsonValue.tryMember "kind" tree)
                (Some(JsonValue.String "CASE"))
                "The durable request declares its subject"

            Expect.equal
                (RequestRecord.encode request)
                encoded
                "Durable bytes match the independent oracle"

            Expect.isError
                (RequestRecord.decode
                    65536
                    (Encoding.UTF8.GetBytes(
                        (Encoding.UTF8.GetString encoded)
                            .Replace("\"kind\":\"CASE\"", "\"kind\":\"AUTHORITY\"")
                    )))
                "The durable subject must agree with the case record grammar")

let private identityForms () =
    withVectors (fun root ->
        let vectors = root.GetProperty("canonicalIdentity").EnumerateArray() |> Seq.toList
        Expect.equal vectors.Length 10 "Every amount carrier and both authority reference acts"

        for vector in vectors do
            let normalize (side: string) =
                let source = bytes (vector.GetProperty side)

                match text vector "subject" with
                | "CASE" ->
                    RequestRecord.decode 65536 source
                    |> Fixtures.accepted
                    |> Case.canonical
                    |> RequestRecord.encode
                | "AUTHORITY" ->
                    AuthorityRecord.decode 65536 source
                    |> Fixtures.accepted
                    |> AuthorityRequests.canonical
                    |> AuthorityRecord.encode
                | _ -> failtest "Unexpected oracle subject"

            let left, right = normalize "left", normalize "right"
            Expect.equal left (bytes (vector.GetProperty "canonicalLeft")) (text vector "name")
            Expect.equal right (bytes (vector.GetProperty "canonicalRight")) (text vector "name")

            Expect.equal
                (left = right)
                (vector.GetProperty("same").GetBoolean())
                "Independent identity equality")

let private malformedIdentity () =
    for amount in [ "01"; "-1"; "1e0"; "NaN"; "" ] do
        let request =
            Fixtures.request
                0L
                (AuthoredCaseAct.Open
                    { Fixtures.registration with
                        ClaimedAmount = amount
                    })

        let wire = ClaimCore.Protocol.RequestCodecs.caseRequest.Write request

        let admitted =
            ClaimCore.Protocol.RequestCodecs.caseRequest.Read "$" wire |> Fixtures.accepted

        Expect.equal admitted request "Wire admission preserves malformed authored scalars"

        let expected =
            if amount = "" then
                Violation.TextRequired
            else
                Violation.DecimalFormat(18, 4)

        Expect.equal
            (Case.parse admitted |> Result.map ignore)
            (Error(Refusal.InvalidInput(InputTarget.Field BusinessField.ClaimedAmount, expected)))
            "The original malformed spelling reaches its field-specific diagnostic"

[<Tests>]
let tests =
    testList
        "canonical durable records"
        [
            testCase "canonical identities match independent Python vectors" identityForms
            testCase "malformed amount spelling is preserved for diagnostics" malformedIdentity
            testCase "durable requests retain their own subject envelope" operationIdentity
            testCase "case requests reproduce independent bytes and meaning" requests
            testCase "snapshots reproduce independent bytes and meaning" snapshot
            testCase "authority requests reproduce independent bytes and meaning" authority
            testCase "unknown formats and noninteger format lexemes refuse" formats
        ]
