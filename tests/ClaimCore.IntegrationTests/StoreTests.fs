module ClaimCore.IntegrationTests.StoreTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreFixtures

let private serviceCases (admin: ServiceContext) principal =
    task {
        let! registered =
            (Core.execute
                admin
                (OperationInput.Authority(authorityRequest (id 400) principal AuthorityAct.Register))
                (CancellationToken.None))

        acceptedAuthority registered

        for number in 1..2 do
            let reference = $"SERVICE-{number}"

            let! opened =
                (Core.execute
                    admin
                    (OperationInput.Case(
                        request (410 + number) reference 0L (AuthoredCaseAct.Open registration)
                    ))
                    (CancellationToken.None))

            accepted opened |> ignore

            let! assigned =
                grant
                    admin
                    (id (420 + number))
                    principal
                    Role.CaseReader
                    (GrantTarget.CaseReference reference)
                    true

            acceptedAuthority assigned
    }

let private serviceScopeCorruption () =
    async {
        let! database = PostgresFixture.create () |> Async.AwaitTask

        try
            let principal =
                PrincipalKey.service "https://issuer.example/" "scoped-service"
                |> Result.defaultWith (string >> failtest)

            let! admin = caseWorker database.Fixture |> Async.AwaitTask
            do! serviceCases admin principal |> Async.AwaitTask

            use! connection =
                database.OwnerSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.grants(actor_id,role,case_id) SELECT $1,$2,case_id FROM claimcore.cases WHERE case_reference=$3"
                    [| box (id 400); box (Role.token Role.DataSteward); box "SERVICE-1" |]
                    CancellationToken.None
                |> Async.AwaitTask

            let service =
                Service.create
                    BuildIdentity.current.Version
                    database.Fixture.InstallationId
                    database.Fixture.Store
                    database.Fixture.Calendar
                    principal

            let! hidden =
                (Core.get service ({ CaseReference = "SERVICE-1" }) (CancellationToken.None))
                |> Async.AwaitTask

            Expect.equal
                hidden
                (ReadReply.Rejected Refusal.ResourceUnavailable)
                "Loaded human-only corruption refuses the scoped view"

            let! visible =
                (Core.list service ({ AfterCursor = None; Limit = 50 }) (CancellationToken.None))
                |> Async.AwaitTask

            match visible with
            | ReadReply.Succeeded page ->
                Expect.equal
                    (page.Items
                     |> List.map (fun (summary: CaseSummary) ->
                         CaseReference.format summary.Reference))
                    [ "SERVICE-2" ]
                    "Assigned SQL cannot disclose through a corrupt human-only row"
            | _ -> failtest "Valid independent case must remain readable"
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL scoped storage"
        [
            ClaimCore.TestSupport.StoreContractTests.tests PostgresFixture.fixture
            testCaseAsync
                "assigned service reads exclude human-only corruption in that scope"
                (serviceScopeCorruption ())
        ]
