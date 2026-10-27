module ClaimCore.IntegrationTests.HistoricalHttpTests

open ClaimCore.Protocol

open ClaimCore.TestSupport

open ClaimCore.TestSupport

open ClaimCore.Json

open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private httpRequest request =
    Codec.encode Api.operationExecute.Input (OperationInput.Case request)

let private outcome (bytes: byte array) =
    use document = JsonDocument.Parse bytes
    document.RootElement.GetProperty("outcome").Clone()

let private caller (database: PostgresFixture.Database) =
    task {
        let! owner = caseWorker database.Fixture

        let principal =
            PrincipalKey.human
                ClaimCore.TestSupport.TestServerFixture.oidc.Issuer.AbsoluteUri
                "synthetic-human"
            |> Result.defaultWith (fun _ -> failtest "Synthetic human")

        let! registered =
            (Service.execute
                owner
                (OperationInput.Authority(authorityRequest (id 20) principal AuthorityAct.Register))
                (token))

        acceptedAuthority registered

        let! granted =
            grant owner (id 21) principal Role.CaseHandler GrantTarget.Installation true

        acceptedAuthority granted
        return core database.Fixture
    }

let private readRetained (host: ClaimCore.TestSupport.TestServerFixture.Host) csrf name =
    task {
        let! status, response =
            host.Call(
                "case.get",
                Encoding.UTF8.GetBytes "{\"caseReference\":\"historical-1\"}",
                csrf
            )

        Expect.equal status 200 "Retained record passes real HTTP self-validation"

        let fields =
            (outcome response).GetProperty("data").GetProperty("state").GetProperty("fields")

        Expect.equal
            (fields.GetProperty("registration").GetProperty("claimantName").GetString())
            name
            "Retained text reaches the client"

    }

let private retainedHistory (database: PostgresFixture.Database) =
    task {
        let! forActor = caller database
        let name = String.replicate 201 "A"
        do! AcceptedHistoryFixtures.seed database name

        use host =
            new ClaimCore.TestSupport.TestServerFixture.Host(true, forActor = forActor)

        let! status, _ = host.Login()
        Expect.equal status 200 "Authenticated synthetic browser"
        let! csrf = host.Token()

        do! readRetained host csrf name

        let values =
            { registration with
                ClaimantName = name
                InsurerName = "Corrected insurer"
            }

        let reaffirm =
            request 11 "HISTORICAL-1" 1L (AuthoredCaseAct.AmendRegistration values)

        let! status, response = host.Call("operation.execute", httpRequest reaffirm, csrf)

        Expect.equal status 200 "Reaffirmation passes intake and response validation"

        Expect.equal
            ((outcome response).GetProperty("kind").GetString())
            "ACCEPTED"
            "Actual locked decision accepts unchanged retained text"

        let changed =
            request
                12
                "HISTORICAL-1"
                2L
                (AuthoredCaseAct.AmendRegistration
                    { values with
                        ClaimantName = name + "B"
                    })

        let! status, response = host.Call("operation.execute", httpRequest changed, csrf)

        Expect.equal status 200 "Admission refusal stays a typed outcome"
        let refusal = outcome response

        Expect.equal
            (refusal.GetProperty("kind").GetString())
            "REJECTED"
            "Changed assertion refused"

        let diagnostic = refusal.GetProperty("data")

        Expect.equal
            (diagnostic.GetProperty("code").GetString())
            "INVALID_INPUT"
            "Current input is rejected"

        let issues = diagnostic.GetProperty("issues").EnumerateArray() |> Seq.toList
        Expect.hasLength issues 1 "Only the changed assertion is invalid"

        Expect.equal
            (issues[0].GetProperty("field").GetString())
            "claimantName"
            "Changed assertion is identified"

        Expect.equal
            (issues[0].GetProperty("code").GetString())
            "TOO_LONG"
            "Current policy owns the refusal"

        let! _ = verified database
        return ()
    }

let private zeroRevisionReply endpoint status response =
    Expect.equal status 200 "An absent-case revision conflict is a typed rejection"

    Expect.isOk
        (StrictJson.read 100000 response |> Result.bind (Api.admitOutput endpoint))
        "The published reply codec admits revision zero"

    use document = JsonDocument.Parse(response: byte array)

    Expect.equal
        (document.RootElement.GetProperty("operationId").GetGuid())
        (id 31)
        "Original operation identity"

    let outcome = document.RootElement.GetProperty("outcome")
    Expect.equal (outcome.GetProperty("kind").GetString()) "REJECTED" "No response encoding failure"
    let diagnostic = outcome.GetProperty("data")

    let mutable target = Unchecked.defaultof<JsonElement>

    Expect.isFalse
        (diagnostic.TryGetProperty("target", &target))
        "A revision conflict has no input target"

    Expect.equal
        (diagnostic.GetProperty("code").GetString())
        "REVISION_CONFLICT"
        "Domain refusal preserved"

    Expect.equal
        (diagnostic.GetProperty("action").GetString())
        "READ_CURRENT"
        "Original corrective action"

    Expect.equal
        (diagnostic.GetProperty("actualRevision").GetInt64())
        0L
        "Absence has revision zero"

let private absentRevision (database: PostgresFixture.Database) =
    task {
        let! forActor = caller database

        use host =
            new ClaimCore.TestSupport.TestServerFixture.Host(true, forActor = forActor)

        let! _ = host.Login()
        let! csrf = host.Token()
        let! before = verified database
        let pending = request 31 "ABSENT-REVISION" 1L (AuthoredCaseAct.Open registration)
        let responses = ResizeArray<_>()

        for name in [ Api.operationPreview.Token; Api.operationExecute.Token ] do
            let! status, response = host.Call(name, httpRequest pending, csrf)

            responses.Add(
                Api.tryFind name |> Option.defaultWith (fun () -> failtest "Endpoint"),
                status,
                response
            )

        let! after = verified database
        Expect.equal after before "Neither rejected operation records a ledger entry"

        let! view =
            inspectCase
                database.Fixture
                (database.Fixture.Owner)
                (Some pending.OperationId)
                (pending.CaseReference)
                (token)

        match view with
        | Ok view ->
            Expect.isNone view.Case "No case projection was created"
            Expect.isNone view.ExistingOperation "A rejected operation ID remains unused"
        | Error _ -> failtest "Expected authoritative refusal evidence"

        for endpoint, status, response in responses do
            zeroRevisionReply endpoint status response
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL HTTP boundaries"
        [
            testCaseAsync
                "reads retained text and refuses changed policy violations"
                (run retainedHistory)
            testCaseAsync
                "absent case revision conflicts remain typed in preview and execution"
                (run absentRevision)
        ]
