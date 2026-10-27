module ClaimCore.Tests.RecordBoundaryTests

open ClaimCore.Protocol

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open System.Globalization
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private recordBytes revision =
    let original =
        request 0L (AuthoredCaseAct.Open registration)
        |> CaseFixtures.encode
        |> Encoding.UTF8.GetString

    original.Replace("\"expectedRevision\":0", "\"expectedRevision\":" + revision)
    |> Encoding.UTF8.GetBytes

let private revisions () =
    for revision in [ "-1"; "9007199254740992"; "1.0"; "1e0"; "\"1\""; "null"; "true" ] do
        Expect.isError
            (RequestRecord.decode 65536 (recordBytes revision))
            "Durable records require safe nonnegative integer lexemes"

    for revision in [ "0"; "-0"; "9007199254740990"; "9007199254740991" ] do
        Expect.isOk
            (RequestRecord.decode 65536 (recordBytes revision))
            "Exact expected revision boundary"

    let zero = RequestRecord.decode 65536 (recordBytes "-0") |> accepted

    Expect.equal
        (RequestRecord.encode zero)
        (CaseFixtures.encode (request 0L (AuthoredCaseAct.Open registration)))
        "Negative zero has one canonical identity"

    let terminal =
        { Case.snapshot (opened ()) with
            Revision = Revision.Maximum
        }

    Expect.equal
        (CaseRecord.decodeSnapshot (CaseRecord.encodeSnapshot terminal))
        (Ok terminal)
        "Terminal snapshots remain readable"

    let encoded = CaseRecord.encodeSnapshot terminal |> Encoding.UTF8.GetString

    for revision in [ "0"; "-1"; "9007199254740992"; "1.0"; "1e0"; "\"1\"" ] do
        let candidate =
            encoded.Replace("\"revision\":9007199254740991", "\"revision\":" + revision)

        Expect.isError
            (CaseRecord.decodeSnapshot (Encoding.UTF8.GetBytes candidate))
            "Accepted revisions require positive safe integer lexemes"

let private textFormats () =
    let original = Case.snapshot (opened ())

    let oldFields =
        { viewFields original with
            Registration =
                { (viewFields original).Registration with
                    ClaimantName = CaseFixtures.name "A‮B"
                }
        }

    let snapshot =
        { original with
            State = CaseState.Active(oldFields)
        }

    Expect.equal
        (CaseRecord.decodeSnapshot (CaseRecord.encodeSnapshot snapshot))
        (Ok snapshot)
        "Snapshot codec never reapplies text policy"

    let reaffirm =
        request
            1L
            (AuthoredCaseAct.AmendRegistration
                { registration with
                    ClaimantName = PartyName.format oldFields.Registration.ClaimantName
                })

    Expect.equal
        (RequestRecord.decode 65536 (CaseFixtures.encode reaffirm))
        (Ok(CaseFixtures.command reaffirm))
        "Request codec preserves grandfatherable assertions"

    let encoded = CaseFixtures.encode reaffirm |> Encoding.UTF8.GetString

    for escaped in
        [
            "\\u0000"
            "\\ufdd0"
            "\\ufffe"
            "\\uffff"
            "\\ud800"
            "\\udc00"
            "\\udbff\\udfff"
        ] do
        let candidate = encoded.Replace("A‮B", "A" + escaped + "B")
        Expect.notEqual candidate encoded "Permanent-format negative control changes content"

        Expect.isError
            (RequestRecord.decode 65536 (Encoding.UTF8.GetBytes candidate))
            "Persisted text refuses noncharacters, NUL and invalid Unicode"

let private envelope () =
    let owner: AuthorityRequestRecord =
        {
            OperationId = Guid.Parse "a0000000-0000-4000-8000-000000000001"
            Target =
                {
                    Kind = "HUMAN"
                    Issuer = "https://issuer.example/"
                    Subject = "owner"
                }
            Act = AuthorityAct.ProvisionOwner
        }

    let durable = AuthorityRecord.encode owner

    Expect.equal
        (AuthorityRecord.decode 65536 durable)
        (Ok owner)
        "Provisioning is retained history"

    let asWire bytes =
        Encoding.UTF8.GetString(bytes: byte array)
        |> fun value -> value.Replace("\"format\":1,", "")

        |> Encoding.UTF8.GetBytes

    let ordinary =
        { owner with
            Act = AuthorityAct.Register
        }

    Expect.isOk
        (Codec.decode
            Limits.MaximumRequestBytes
            Api.operationExecute.Input
            (AuthorityRecord.encode ordinary |> asWire))
        "The control envelope is otherwise valid wire input"

    Expect.isError
        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input (asWire durable))
        "Provisioning is never case-work authority API input"

    let case = request 0L (AuthoredCaseAct.Open registration) |> CaseFixtures.encode

    Expect.isError
        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input case)
        "Durable format is not a wire member"

    let authored =
        { request 1L AuthoredCaseAct.Close with
            CaseReference = "unit-001"
        }

    Expect.isTrue
        (CaseFixtures.encode authored
         <> CaseFixtures.encode (request 1L AuthoredCaseAct.Close))
        "Canonical request identity preserves authored reference case"

let private culture () =
    let original = CultureInfo.CurrentCulture
    let replacement = CultureInfo("ar-EG").Clone() :?> CultureInfo
    replacement.NumberFormat.NegativeSign <- "~"
    let request = request 0L (AuthoredCaseAct.Open registration)
    let expected = CaseFixtures.encode request

    try
        CultureInfo.CurrentCulture <- replacement

        Expect.isTrue
            (CaseFixtures.encode request = expected)
            "Durable request bytes are culture independent"

        Expect.isOk (RequestRecord.decode 65536 expected) "Durable parsing is culture independent"
    finally
        CultureInfo.CurrentCulture <- original

let private permanentLimits () =
    use vectors =
        System.Text.Json.JsonDocument.Parse(
            System.IO.File.ReadAllBytes(
                System.IO.Path.Combine(
                    RepositoryRoot.find (),
                    "tests/fixtures/record-format-vectors.json"
                )
            )
        )

    let entries =
        vectors.RootElement.GetProperty("permanentLimits").EnumerateArray()
        |> Seq.toArray

    Expect.equal entries.Length 49 "Every authored scalar boundary is discovered"

    for entry in entries do
        let text name =
            nonNull (entry.GetProperty(name: string).GetString())

        let bytes = Encoding.UTF8.GetBytes(text "utf8")
        Expect.equal (Convert.ToHexStringLower bytes) (text "hex") "Independent byte vector"

        Expect.equal
            (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData bytes))
            (text "sha256")
            "Independent byte digest"

        let valid =
            match text "subject" with
            | "CASE" -> RequestRecord.decode Int32.MaxValue bytes |> Result.isOk
            | "SNAPSHOT" -> CaseRecord.decodeSnapshot bytes |> Result.isOk
            | "AUTHORITY" ->
                AuthorityRecord.decode Int32.MaxValue bytes
                |> Result.bind (
                    AuthorityRecord.interpret
                    >> Result.mapError (fun _ ->
                        {
                            DecodeProblem.Field = "$"
                            Message = "Invalid permanent authority input"
                        })
                )
                |> Result.isOk
            | other -> failtest ("Unexpected authored vector subject: " + other)

        Expect.equal valid (entry.GetProperty("accepted").GetBoolean()) (text "name")

let private independentNames () =
    let request =
        request 1L (AuthoredCaseAct.AmendRegistration registration)
        |> CaseFixtures.command

    let before = RequestRecord.encode request

    let wireNames kind =
        if kind = CaseActKind.AmendRegistration then
            "AMEND"
        else
            CaseActKind.token kind

    let renamedWire = RequestCodecs.caseActWithNames wireNames
    let wire = Codec.encode renamedWire request.Authored.Act |> Encoding.UTF8.GetString

    Expect.stringContains
        wire
        "\"kind\":\"AMEND\""
        "The production wire codec construction receives the scratch name"

    Expect.equal
        (RequestRecord.encode request)
        before
        "Wire spelling does not change durable encoding"

    Expect.stringContains
        (Encoding.UTF8.GetString before)
        "AMEND_REGISTRATION"
        "Durable spelling remains the format-1 literal"

    Expect.isTrue
        (Limits.MaximumRequestBytes <= RecordLimits.MaximumRequestRecordBytes)
        "Transport cannot admit more bytes than the permanent record ceiling"

    let oversized = Array.create (RecordLimits.MaximumRequestRecordBytes + 1) 32uy

    Expect.isError
        (RequestRecord.decode Int32.MaxValue oversized)
        "Callers cannot expand the permanent record decode ceiling"

    let authority: AuthorityRequestRecord =
        {
            OperationId = Guid.Parse "a0000000-0000-4000-8000-000000000001"
            Target =
                {
                    Kind = "HUMAN"
                    Issuer = String('a', RecordLimits.MaximumRequestRecordBytes)
                    Subject = "boundary"
                }
            Act = AuthorityAct.Register
        }

    Expect.throwsT<InvalidOperationException>
        (fun () -> AuthorityRecord.encode authority |> ignore)
        "Oversized encoding is an internal fault"

let private kindRoundTrips () =
    let cases: (AuthoredCaseAct * string) list =
        [
            AuthoredCaseAct.Open registration, "OPEN"
            AuthoredCaseAct.AmendRegistration registration, "AMEND_REGISTRATION"
            AuthoredCaseAct.Correct
                {
                    Registration = Amendment.Keep
                    Decision = Change.Keep
                    Payment = Change.Keep
                },
            "CORRECT"
            AuthoredCaseAct.Decide Fixtures.decision, "DECIDE"
            AuthoredCaseAct.WithdrawDecision, "WITHDRAW_DECISION"
            AuthoredCaseAct.RecordPayment { PaymentDate = "2026-08-20" }, "RECORD_PAYMENT"
            AuthoredCaseAct.ClearPayment, "CLEAR_PAYMENT"
            AuthoredCaseAct.Close, "CLOSE"
            AuthoredCaseAct.Reopen, "REOPEN"
            AuthoredCaseAct.Void "boundary", "VOID"
            AuthoredCaseAct.Reinstate "boundary", "REINSTATE"
            AuthoredCaseAct.Erase, "ERASE"
        ]

    Expect.equal
        (cases |> List.map (fst >> CaseAct.kind) |> Set.ofList)
        (Set.ofList CaseActKind.all)
        "Every case kind is independently authored"

    for act, token in cases do
        let command = request 1L act |> CaseFixtures.command
        let bytes = RequestRecord.encode command

        Expect.equal
            (RequestRecord.decode Int32.MaxValue bytes)
            (Ok command)
            ("Round trip " + token)

        Expect.stringContains
            (Encoding.UTF8.GetString bytes)
            ("\"kind\":\"" + token + "\"")
            "Frozen case token"

    let grant =
        {
            GrantChange.Role = "CASE_READER"
            Scope = GrantTarget.Installation
        }

    let acts: (AuthoredAuthorityAct * string) list =
        [
            AuthoredAuthorityAct.ProvisionOwner, "PROVISION_OWNER"
            AuthoredAuthorityAct.Register, "REGISTER"
            AuthoredAuthorityAct.Grant grant, "GRANT"
            AuthoredAuthorityAct.Revoke grant, "REVOKE"
            AuthoredAuthorityAct.Enable, "ENABLE"
            AuthoredAuthorityAct.Disable, "DISABLE"
        ]

    Expect.equal
        (acts |> List.map (fst >> AuthorityAct.kind) |> Set.ofList)
        (Set.ofList AuthorityKind.all)
        "Every authority kind is independently authored"

    for act, token in acts do
        let record: AuthorityRequestRecord =
            {
                OperationId = Guid.Parse "a0000000-0000-4000-8000-000000000001"
                Target = Inputs.principal
                Act = act
            }

        let bytes = AuthorityRecord.encode record

        Expect.equal
            (AuthorityRecord.decode Int32.MaxValue bytes)
            (Ok record)
            ("Round trip " + token)

        Expect.stringContains
            (Encoding.UTF8.GetString bytes)
            ("\"kind\":\"" + token + "\"")
            "Frozen authority token"

[<Tests>]
let formatOwnership =
    testList
        "permanent record ownership"
        [
            testCase "every kind has an independently authored durable round trip" kindRoundTrips
            testCase "independent scalar vectors protect every permanent boundary" permanentLimits
            testCase
                "wire naming and transport limits do not control durable records"
                independentNames
        ]

[<Tests>]
let tests =
    testList
        "durable format boundaries"
        [
            testCase "raw revision lexemes and safe integer bounds are permanent" revisions
            testCase
                "grandfatherable text and permanent Unicode have separate boundaries"
                textFormats
            testCase
                "wire envelopes exclude provisioning and retain exact authored content"
                envelope
            testCase "record bytes and parsing do not consult ambient culture" culture
        ]
