module ClaimCore.TestSupport.MetricsAcceptance

open System
open System.Net.Http
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Protocol
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.TestSupport.TestServerFixture

let private token = CancellationToken.None

let private sql (database: PostgresFixture.Database) statement =
    task {
        use! connection = database.OwnerSource.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection statement [||] token
        return ()
    }

let private call (host: Host) (endpoint: Endpoint<'Input, 'Output>) input antiforgery expected =
    task {
        let! status, bytes =
            host.Call(endpoint.Token, Codec.encode endpoint.Input input, antiforgery)

        Expect.equal status 200 "A dispatched acceptance call returns a typed reply"

        let wire =
            StrictJson.read Limits.MaximumResponseBytes bytes
            |> Result.defaultWith (fun _ -> failtest "Invalid server JSON")

        let classification =
            (Route.describe endpoint).ClassifyOutput wire
            |> Result.defaultWith (fun _ -> failtest "Invalid server reply")

        Expect.equal
            classification.Kind
            expected
            "The observed outcome belongs to the real acceptance call"
    }

let private logout (host: Host) =
    task {
        let! antiforgery = host.Token()
        do! call host Api.logout () antiforgery ReplyKind.Succeeded
    }

let private caseFlow (database: PostgresFixture.Database) storage =
    task {
        use host =
            new Host(
                true,
                forActor = core database.Fixture,
                sessions = storage,
                installationId = database.Fixture.InstallationId
            )

        let! _ = host.Login()
        let! antiforgery = host.Token()

        let operation =
            OperationInput.Case(
                request 601 "METRIC-ACCEPTANCE" 0L (AuthoredCaseAct.Open registration)
            )

        do! call host Api.operationPreview operation antiforgery ReplyKind.Reviewed
        do! call host Api.operationExecute operation antiforgery ReplyKind.Accepted
        do! call host Api.operationExecute operation antiforgery ReplyKind.AlreadyAccepted

        do!
            call
                host
                Api.caseGet
                { CaseReference = "METRIC-ACCEPTANCE" }
                antiforgery
                ReplyKind.Succeeded

        do!
            call
                host
                Api.caseGet
                { CaseReference = "METRIC-MISSING" }
                antiforgery
                ReplyKind.Rejected

        let page =
            Api.caseList.Input.Read "$" (JsonValue.Object [ "limit", JsonValue.Integer 50L ])
            |> Result.defaultWith (fun _ -> failtest "The minimal list input must admit")

        do! call host Api.caseList page antiforgery ReplyKind.Succeeded
        host.HandlerStub.RuntimeAvailable <- false

        do!
            call
                host
                Api.caseGet
                { CaseReference = "METRIC-ACCEPTANCE" }
                antiforgery
                ReplyKind.Failed

        host.HandlerStub.RuntimeAvailable <- true
        let! status, _ = host.Send(HttpMethod.Get, "/api/not-a-real-endpoint", None, [])
        Expect.equal status 404 "An unknown API path exercises the bounded unknown endpoint"
        do! logout host
    }

let private endedFlow (database: PostgresFixture.Database) storage statement =
    task {
        use host =
            new Host(
                true,
                forActor = core database.Fixture,
                sessions = storage,
                installationId = database.Fixture.InstallationId
            )

        let! _ = host.Login()
        do! sql database statement
        let! status, _ = host.Bootstrap()
        Expect.equal status 200 "An ended ticket still permits public session discovery"
    }

let run () =
    task {
        use observed = new MetricObservations.Capture()

        let principal =
            PrincipalKey.human oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (string >> failtest)

        let! database = PostgresFixture.createFor principal

        try
            let! _ = caseWorker database.Fixture
            let storage = SessionStore(database.RuntimeSource) :> ISessionStore
            do! caseFlow database storage

            do!
                endedFlow
                    database
                    storage
                    "UPDATE claimcore.sessions SET created_at=clock_timestamp()-interval '1 hour',last_activity=clock_timestamp()-interval '31 minutes'"

            do!
                endedFlow
                    database
                    storage
                    "UPDATE claimcore.sessions SET protected_ticket=decode('00','hex')"

            return observed.Values
        finally
            database.Fixture.Dispose()
    }
