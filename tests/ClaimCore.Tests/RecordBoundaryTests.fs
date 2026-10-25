module ClaimCore.Tests.RecordBoundaryTests

open ClaimCore.Contract

open System
open System.Globalization
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private recordBytes revision =
    let original =
        request 0L (CaseAct.Open registration)
        |> RequestRecord.encode
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
        (RequestRecord.encode (request 0L (CaseAct.Open registration)))
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
                    ClaimantName = "A\u202eB"
                }
        }

    let snapshot =
        { original with
            View = CaseView.Live(oldFields, Disposition.Active)
        }

    Expect.equal
        (CaseRecord.decodeSnapshot (CaseRecord.encodeSnapshot snapshot))
        (Ok snapshot)
        "Snapshot codec never reapplies text policy"

    let reaffirm =
        request
            1L
            (CaseAct.AmendRegistration
                { registration with
                    ClaimantName = oldFields.Registration.ClaimantName
                })

    Expect.equal
        (RequestRecord.decode 65536 (RequestRecord.encode reaffirm))
        (Ok reaffirm)
        "Request codec preserves grandfatherable assertions"

    let encoded = RequestRecord.encode reaffirm |> Encoding.UTF8.GetString

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
        let candidate = encoded.Replace("A\u202eB", "A" + escaped + "B")
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
            Act = RecordedAuthorityAct.ProvisionOwner
        }

    let durable = AuthorityRecord.encode owner

    Expect.equal
        (AuthorityRecord.decode 65536 durable)
        (Ok owner)
        "Provisioning is retained history"

    let wire =
        Encoding.UTF8.GetString durable
        |> fun value -> value.Replace("\"format\":3,", "") |> Encoding.UTF8.GetBytes

    Expect.isError
        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input wire)
        "Provisioning is never case-work authority API input"

    let case = request 0L (CaseAct.Open registration) |> RequestRecord.encode

    Expect.isError
        (Codec.decode Limits.MaximumRequestBytes Api.operationExecute.Input case)
        "Durable format is not a wire member"

    let authored =
        { request 1L CaseAct.Close with
            CaseReference = "unit-001"
        }

    Expect.isTrue
        (RequestRecord.encode authored <> RequestRecord.encode (request 1L CaseAct.Close))
        "Canonical request identity preserves authored reference case"

let private culture () =
    let original = CultureInfo.CurrentCulture
    let replacement = CultureInfo("ar-EG").Clone() :?> CultureInfo
    replacement.NumberFormat.NegativeSign <- "~"
    let request = request 0L (CaseAct.Open registration)
    let expected = RequestRecord.encode request

    try
        CultureInfo.CurrentCulture <- replacement

        Expect.isTrue
            (RequestRecord.encode request = expected)
            "Durable request bytes are culture independent"

        Expect.isOk (RequestRecord.decode 65536 expected) "Durable parsing is culture independent"
    finally
        CultureInfo.CurrentCulture <- original

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
