module ClaimCore.IntegrationTests.CommittedCancellationTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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
                    Submissions.createRaw
                        "case.open"
                        (OperationIds.fresh ())
                        (OperationBinding.CaseNew 0L)
                        (Submissions.fields (RegistrationInput.values registration))
                    Submissions.createRaw
                        "people.register"
                        (OperationIds.fresh ())
                        (OperationBinding.People(None, 0L))
                        (Map.fold
                            (fun values key value -> Map.add key value values)
                            (Submissions.principal target)
                            (Submissions.profile DomainInputs.actorProfile))
                ]

            for input in inputs do
                let! database = PostgresFixture.create () |> Async.AwaitTask

                try
                    let! _ = caseWorker database.Fixture |> Async.AwaitTask
                    use cancellation = new CancellationTokenSource()
                    let inner = database.Fixture.Store

                    let probe =
                        { new IStore with
                            member _.RecordDisclosure(event, token) =
                                inner.RecordDisclosure(event, token)

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
                                     WriteOperation.RequiresEncryption = needsEncryption
                                     WriteOperation.Work = work
                                     CancellationToken = ct
                                 })
                                =
                                task {
                                    let! result =
                                        inner.Write(
                                            {
                                                WriteOperation.RequiresEncryption = needsEncryption
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
                        (Service.executeSubmission core (input) (cancellation.Token))
                        |> Async.AwaitTask

                    let! observed =
                        Service.operationGet
                            core
                            { OperationId = input.OperationId }
                            CancellationToken.None
                        |> Async.AwaitTask

                    match reply.Outcome, observed.Outcome with
                    | ExecuteResult.ReceiptUnavailable,
                      Outcome.Succeeded(OperationLookupResult.Accepted(stored, true)) ->
                        Expect.equal
                            (OperationId.value stored.OperationId)
                            input.OperationId
                            "The existing operation is found by its original identity"
                    | _ ->
                        failtestf
                            "Cancellation must suppress the receipt and preserve settlement: %A / %A"
                            reply.Outcome
                            observed.Outcome

                    Expect.isTrue
                        cancellation.IsCancellationRequested
                        "Cancellation occurred at port completion"
                finally
                    database.Fixture.Dispose()
        })
