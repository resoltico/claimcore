module ClaimCore.IntegrationTests.ActorResponsibilityTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCaseAsync
        "PostgreSQL responsibility counts and executable links preserve viewer scope"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                let corrupt actor caseId =
                    task {
                        use! connection = database.OwnerSource.OpenConnectionAsync()

                        let! changed =
                            DatabaseSession.execute
                                connection
                                "INSERT INTO claimcore.grants(actor_id,role,case_id) VALUES ($1,'DATA_STEWARD',$2)"
                                [| box (ActorId.value actor); box (CaseId.value caseId) |]
                                CancellationToken.None

                        Expect.equal
                            changed
                            1
                            "The negative control inserts one forbidden scoped projection role"
                    }

                do! ActorResponsibilityScenarios.run database.Fixture corrupt |> Async.AwaitTask
            finally
                database.Fixture.Dispose()
        })
