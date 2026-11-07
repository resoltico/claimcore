module ClaimCore.IntegrationTests.CommittedCancellationTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures

[<Tests>]
let tests =
    testCaseAsync
        "completed acceptance survives cancellation before delivery"
        (async {
            let target =
                PrincipalKey.human "https://issuer.example/" "other"
                |> Result.defaultWith (string >> failtest)

            let inputs =
                [
                    Inputs.caseOperation (
                        request 470 "BOUNDARY" 0L (AuthoredCaseAct.Open registration)
                    )
                    OperationInput.Authority(
                        authorityRequest (id 471) target (AuthorityAct.Register Inputs.actorProfile)
                    )
                ]

            for input in inputs do
                let! database = PostgresFixture.create () |> Async.AwaitTask

                try
                    let! _ = caseWorker database.Fixture |> Async.AwaitTask
                    use cancellation = new CancellationTokenSource()
                    let inner = database.Fixture.Store

                    let probe =
                        { new IStore with
                            member _.Context query = inner.Context query

                            member _.Read
                                ({
                                     ReadOperation.Work = work
                                     CancellationToken = ct
                                 })
                                =
                                inner.Read(
                                    {
                                        ReadOperation.Work = work
                                        CancellationToken = ct
                                    }
                                )

                            member _.Write
                                ({
                                     WriteOperation.Work = work
                                     CancellationToken = ct
                                 })
                                =
                                task {
                                    let! result =
                                        inner.Write(
                                            {
                                                WriteOperation.Work = work
                                                CancellationToken = ct
                                            }
                                        )

                                    cancellation.Cancel()
                                    return result
                                }
                        }

                    let core =
                        Service.create
                            "cancellation-test"
                            database.Fixture.InstallationId
                            probe
                            database.Fixture.Calendar
                            database.Fixture.Owner

                    let! reply =
                        (Service.execute core (input) (cancellation.Token)) |> Async.AwaitTask

                    let! observed =
                        ClaimCore.TestSupport.StoreFixtures.observe
                            core
                            {
                                OperationId = OperationInput.operationId input
                            }
                            CancellationToken.None
                        |> Async.AwaitTask

                    match reply.Outcome, observed with
                    | ExecuteResult.Accepted receipt, Outcome.Succeeded stored ->
                        expectReceipt
                            stored
                            receipt
                            "Completed acceptance is forwarded despite cancellation"
                    | _ -> failtest "Committed acceptance and stored receipt must agree"

                    Expect.isTrue
                        cancellation.IsCancellationRequested
                        "Cancellation occurred at port completion"
                finally
                    database.Fixture.Dispose()
        })
