module ClaimCore.IntegrationTests.StoreTests

open ClaimCore.Contract

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.TestSupport.StoreContractTests

let private serviceCases (admin: IActorClaimsCore) principal =
    task {
        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 400) principal AdmittedAuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered

        for number in 1..2 do
            let reference = $"SERVICE-{number}"

            let! opened =
                admin.Execute(
                    request (410 + number) reference 0L (CaseAct.Open registration),
                    CancellationToken.None
                )

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
                |> Result.defaultWith failtest

            let! admin = caseWorker database.Fixture |> Async.AwaitTask
            do! serviceCases admin principal |> Async.AwaitTask

            use! connection =
                database.OwnerSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.grants(actor_id,role,case_id) SELECT $1,$2,case_id FROM claimcore.cases WHERE case_reference=$3"
                    [| box (id 400); box (RoleTokens.token Role.DataSteward); box "SERVICE-1" |]
                    CancellationToken.None
                |> Async.AwaitTask

            let service =
                Core.create
                    database.Fixture.InstallationId
                    database.Fixture.Store
                    database.Fixture.Calendar
                    principal

            let! hidden =
                service.Get({ CaseReference = "SERVICE-1" }, CancellationToken.None)
                |> Async.AwaitTask

            Expect.equal
                hidden
                (ReadReply.Rejected Refusal.ResourceUnavailable)
                "Loaded human-only corruption refuses the scoped view"

            let! visible =
                service.List({ AfterCursor = None; Limit = 50 }, CancellationToken.None)
                |> Async.AwaitTask

            match visible with
            | ReadReply.Succeeded page ->
                Expect.equal
                    (page.Items
                     |> List.map (function
                         | CaseSummary.Live(reference, _, _, _)
                         | CaseSummary.Erased(reference, _) -> reference))
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
