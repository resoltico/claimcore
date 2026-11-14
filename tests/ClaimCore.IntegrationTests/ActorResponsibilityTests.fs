module ClaimCore.IntegrationTests.ActorResponsibilityTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCaseAsync
        "PostgreSQL responsibility counts and executable links preserve viewer scope"
        (async {
            let! database = PostgresFixture.create () |> Async.AwaitTask

            try
                let observed = ObservedTransactions.Store(database.Fixture.Store)

                let fixture =
                    { database.Fixture with
                        Store = observed
                    }

                let corrupt actor caseId =
                    task {
                        use! connection = database.OwnerSource.OpenConnectionAsync()

                        let! changed =
                            DatabaseSession.execute
                                connection
                                "INSERT INTO claimcore.grants(actor_id,role,case_id,granted_sequence,granted_effect_index) VALUES ($1,'DATA_STEWARD',$2,1,0)"
                                [| box (ActorId.value actor); box (CaseId.value caseId) |]
                                CancellationToken.None

                        Expect.equal
                            changed
                            1
                            "The negative control inserts one forbidden scoped projection role"
                    }

                do! ActorResponsibilityScenarios.run fixture corrupt |> Async.AwaitTask

                for subject in [ "owner-only-viewer"; "global-viewer" ] do
                    let principal =
                        PrincipalKey.human "https://issuer.example/" subject
                        |> Result.defaultWith (string >> failtest)

                    let before = observed.Calls

                    let! result =
                        Service.actors
                            (StoreFixtures.core fixture principal)
                            {
                                Role = None
                                Text = None
                                AfterCursor = None
                                Limit = 50
                            }
                            CancellationToken.None
                        |> Async.AwaitTask

                    match result with
                    | Outcome.Succeeded _ -> ()
                    | other -> failtestf "Expected directory result: %A" other

                    Expect.equal
                        (observed.Calls - before)
                        1
                        "People, directory and enrichment share one provider transaction"
            finally
                database.Fixture.Dispose()
        })
