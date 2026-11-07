module ClaimCore.Tests.RecordBoundaryTests

open ClaimCore.Protocol

open ClaimCore.TestSupport

open ClaimCore.Json

open System
open System.Globalization
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
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
            Revision = CaseFixtures.revision 9007199254740991L
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
                (ClaimCore.TestSupport.Inputs.registrationStateWith
                    [ BusinessField.ClaimantName, Admitted.Name(CaseFixtures.name "A‮B") ]
                    ((viewFields original).Registration))
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
            (AuthoredCaseAct.AmendRegistration(
                Inputs.registrationUpdate (
                    ClaimCore.TestSupport.Inputs.registrationWith
                        [
                            BusinessField.ClaimantName,
                            (PartyName.format (Registration.claimant (oldFields.Registration)))
                        ]
                        (registration)
                )
            ))

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
    let id = Guid.Parse "a0000000-0000-4000-8000-000000000001"

    let principal =
        PrincipalKey.human "https://issuer.example/" "owner" |> Fixtures.accepted

    let owner: AuthorityEffectRecord =
        {
            OperationId = id
            TargetActorId = CaseFixtures.actorId id
            Effect = AuthorityEffect.ProvisionOwner(PrincipalKey.toInput principal)
        }

    let durable = AuthorityRecord.encode owner

    Expect.equal
        (AuthorityRecord.decode 65536 durable)
        (Ok owner)
        "Identity-only provisioning effect roundtrip"

    let ordinary =
        { owner with
            Effect = AuthorityEffect.Register(PrincipalKey.toInput principal)
        }

    let asWire bytes =
        Encoding.UTF8.GetString(bytes: byte[])
        |> fun value -> value.Replace("\"format\":3,", "") |> Encoding.UTF8.GetBytes

    Expect.isError
        (Codec.decode
            Limits.MaximumRequestBytes
            Api.operationExecute.Input
            (AuthorityRecord.encode ordinary |> asWire))
        "A durable identity effect is not an authored registration request"

    Expect.isError
        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input (asWire durable))
        "Provisioning has no ordinary endpoint"

    let authored =
        Inputs.authorityRequest id principal (AuthorityAct.Register Inputs.actorProfile)
        |> OperationInput.Authority

    let bytes = Api.operationExecute.Input.Write authored |> WireWriter.bytes

    Expect.equal
        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input bytes)
        (Ok authored)
        "Required authored profile is present on the normal request"

    let case = request 0L (AuthoredCaseAct.Open registration) |> CaseFixtures.encode

    Expect.isError
        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input case)
        "Durable format is not a wire member"

    let authored =
        { request 1L (AuthoredCaseAct.Bare BareAct.Close) with
            CaseReference = "unit-001"
        }

    Expect.isTrue
        (CaseFixtures.encode authored
         <> CaseFixtures.encode (request 1L (AuthoredCaseAct.Bare BareAct.Close)))
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

let private independentNames () =
    let request =
        request 1L (AuthoredCaseAct.AmendRegistration(Inputs.registrationUpdate registration))
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

    let authority: AuthorityEffectRecord =
        {
            OperationId = Guid.Parse "a0000000-0000-4000-8000-000000000001"
            TargetActorId = CaseFixtures.actorId (Guid.Parse "a0000000-0000-4000-8000-000000000001")
            Effect =
                AuthorityEffect.Register
                    {
                        Kind = PrincipalKind.Human
                        Issuer = String('a', RecordLimits.MaximumRequestRecordBytes)
                        Subject = "boundary"
                    }
        }

    Expect.throwsT<ArgumentException>
        (fun () -> AuthorityRecord.encode authority |> ignore)
        "Invalid permanent identity cannot be encoded"

    Expect.isError
        (AuthorityRecord.decode Int32.MaxValue oversized)
        "An authority decoder cannot expand the permanent record ceiling"

let private kindRoundTrips () =
    let cases = Inputs.caseActs registration Fixtures.decision Inputs.payment "boundary"

    Expect.equal
        (cases |> List.map (fst >> CaseAct.kind) |> Set.ofList)
        (Set.ofList CaseActKind.all)
        "Every case kind is independently authored"

    for act, token in cases do
        Expect.equal
            (DurableNames.caseAct (CaseAct.kind act))
            token
            "Independent permanent act token"

        Expect.equal
            (DurableNames.tryCaseAct token)
            (Some(CaseAct.kind act))
            "Permanent act inverse"

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
            RecordedGrant.Role = Role.CaseReader
            Scope = RecordedGrantScope.Installation
        }

    let acts = Inputs.authorityEffects [ grant ]

    Expect.equal
        (acts |> List.map (fst >> AuthorityEffect.kind) |> Set.ofList)
        (AuthorityKind.all |> List.filter ((<>) AuthorityKind.Admit) |> Set.ofList)
        "Every durable authority effect is independently authored; ADMIT is a grouped root"

    for act, token in acts do
        Expect.equal
            (DurableNames.authorityAct (AuthorityEffect.kind act))
            token
            "Independent permanent authority token"

        Expect.equal
            (DurableNames.tryAuthorityAct token)
            (Some(AuthorityEffect.kind act))
            "Permanent authority inverse"

        let record: AuthorityEffectRecord =
            {
                OperationId = Guid.Parse "a0000000-0000-4000-8000-000000000001"
                TargetActorId =
                    CaseFixtures.actorId (Guid.Parse "a0000000-0000-4000-8000-000000000001")
                Effect = act
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
