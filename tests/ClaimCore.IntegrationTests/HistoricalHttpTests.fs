module ClaimCore.IntegrationTests.HistoricalHttpTests

open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.TestSupport

open ClaimCore.TestSupport

open ClaimCore.Json

open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private httpRequest request =
    Codec.encode Api.operationExecute.Input request

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
            ((task {
                let! submission =
                    ClaimCore.TestSupport.StoreFixtures.personSubmission
                        (database.Fixture.Store)
                        ((id database.Fixture 20))
                        (principal)
                        (None)
                        "people.register"
                        (ClaimCore.TestSupport.Submissions.profile (
                            ClaimCore.TestSupport.DomainInputs.actorProfile
                        ))

                return! Service.executeSubmission (owner) submission ((token))
            }))

        acceptedPeople registered

        let! granted =
            grant
                owner
                (id database.Fixture 21)
                principal
                {
                    Role = Role.CaseHandler
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted
        return core database.Fixture
    }

let private readRetained (host: ClaimCore.TestSupport.TestServerFixture.Host) csrf name =
    task {
        let! status, response =
            host.Call(
                "case.get",
                Encoding.UTF8.GetBytes
                    "{\"target\":{\"kind\":\"REFERENCE\",\"caseReference\":\"historical-1\"}}",
                csrf
            )

        Expect.equal status 200 "Retained record passes real HTTP self-validation"

        let fields =
            (outcome response).GetProperty("data").GetProperty("state").GetProperty("fields")

        Expect.equal
            (fields.GetProperty("registration").GetProperty("policyNumber").GetString())
            name
            "Retained text reaches the client"

    }

let private retainedHistory (database: PostgresFixture.Database) =
    task {
        let! forActor = caller database
        let name = String.replicate 200 "A"
        do! AcceptedHistoryFixtures.seed database name

        use host =
            new ClaimCore.TestSupport.TestServerFixture.Host(
                true,
                forActor = forActor,
                installationId = database.Fixture.InstallationId
            )

        let! status, _ = host.Login()
        Expect.equal status 200 "Authenticated synthetic browser"
        let! csrf = host.Token()

        do! readRetained host csrf name

        let values =
            (ClaimCore.TestSupport.DomainInputs.registrationWith
                [
                    BusinessField.PolicyNumber, (name)
                    BusinessField.InsurerClaimReference, ("Corrected insurer")
                ]
                (registration))

        let reaffirm =
            request
                (database.Fixture)
                (11)
                ("HISTORICAL-1")
                (1L)
                "case.amendRegistration"
                (ClaimCore.TestSupport.Submissions.updates (
                    RegistrationUpdateInput.values ((DomainInputs.registrationUpdate values))
                ))

        let! status, response = host.Call("operation.execute", httpRequest reaffirm, csrf)

        Expect.equal status 200 "Reaffirmation passes intake and response validation"

        Expect.equal
            ((outcome response).GetProperty("kind").GetString())
            "ACCEPTED"
            "Actual locked decision accepts unchanged retained text"

        let changed =
            request
                (database.Fixture)
                (12)
                ("HISTORICAL-1")
                (2L)
                "case.amendRegistration"
                (ClaimCore.TestSupport.Submissions.updates (
                    RegistrationUpdateInput.values (
                        (DomainInputs.registrationUpdate (
                            ClaimCore.TestSupport.DomainInputs.registrationWith
                                [ BusinessField.PolicyNumber, (name + "B") ]
                                (values)
                        ))
                    )
                ))

        let! status, response = host.Call("operation.execute", httpRequest changed, csrf)

        Expect.equal status 200 "Admission refusal stays a typed outcome"
        let refusal = outcome response

        Expect.equal
            (refusal.GetProperty("kind").GetString())
            "REJECTED"
            "Changed assertion refused"

        let diagnostic = refusal.GetProperty("rejection")

        Expect.equal
            (diagnostic.GetProperty("code").GetString())
            "INVALID_INPUT"
            "Current input is rejected"

        let issues = diagnostic.GetProperty("issues").EnumerateArray() |> Seq.toList
        Expect.hasLength issues 1 "Only the changed assertion is invalid"

        Expect.equal
            (issues[0].GetProperty("input").GetString())
            "field:policyNumber"
            "Changed assertion is identified"

        Expect.equal
            (issues[0].GetProperty("code").GetString())
            "TOO_LONG"
            "Current policy owns the refusal"

        let! _ = verified database
        return ()
    }

let private invalidCreationRevisionReply status response =
    Expect.equal status 400 "Creation only admits revision zero at the binding boundary"
    use document = JsonDocument.Parse(response: byte array)

    Expect.equal
        (document.RootElement.GetProperty("hostFailure").GetProperty("code").GetString())
        "INPUT_INVALID"
        "A nonzero creation binding is invalid before domain execution"

let private absentRevision (database: PostgresFixture.Database) =
    task {
        let! forActor = caller database

        use host =
            new ClaimCore.TestSupport.TestServerFixture.Host(
                true,
                forActor = forActor,
                installationId = database.Fixture.InstallationId
            )

        let! _ = host.Login()
        let! csrf = host.Token()
        let! before = verified database

        let pending =
            request
                (database.Fixture)
                (31)
                ("ABSENT-REVISION")
                (0L)
                "case.open"
                (ClaimCore.TestSupport.Submissions.fields (RegistrationInput.values (registration)))

        let responses = ResizeArray<_>()

        for name in [ Api.operationPreview.Token; Api.operationExecute.Token ] do
            let! status, response =
                host.Call(
                    name,
                    httpRequest pending
                    |> Encoding.UTF8.GetString
                    |> fun json -> json.Replace("\"expectedRevision\":0", "\"expectedRevision\":1")
                    |> Encoding.UTF8.GetBytes,
                    csrf
                )

            responses.Add(
                Api.tryFind name |> Option.defaultWith (fun () -> failtest "Endpoint"),
                status,
                response
            )

        let! after = verified database
        Expect.equal after before "Neither rejected operation records a ledger entry"

        let! view =
            database.Fixture.Store.Read
                {
                    Work =
                        fun tx ->
                            task {
                                let! current = FamilyReads.findCaseByReference tx "ABSENT-REVISION"

                                let! operation =
                                    LedgerReads.findOperation
                                        tx
                                        (CaseFixtures.operationId pending.OperationId)

                                return current, operation
                            }
                    CancellationToken = token
                }

        match view with
        | Ok(current, operation) ->
            Expect.isNone current "No case projection was created"
            Expect.isNone operation "A rejected operation ID remains unused"
        | Error _ -> failtest "Expected authoritative refusal evidence"

        for _, status, response in responses do
            invalidCreationRevisionReply status response
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
                "nonzero creation revision is refused before preview and execution"
                (run absentRevision)
        ]
