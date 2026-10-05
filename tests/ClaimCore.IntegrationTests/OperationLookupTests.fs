module ClaimCore.IntegrationTests.OperationLookupTests

open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.Fixtures

let tests =
    testList
        "operation lookup"
        [
            testCase "operation lookup identifies exact accepted result" (fun () ->
                use database = store ()
                let service = database :> IClaimStore
                let request = newRequest ()

                CommandExecution.executeAsync service clock request
                |> await
                |> accepted
                |> ignore

                let result =
                    service.Operation(request.OperationId, CancellationToken.None)
                    |> await
                    |> accepted

                let reference =
                    result
                    |> Option.map (fun value -> (Claim.view value.Case).Fields.CaseReference)

                Expect.isTrue (reference = Some request.CaseReference) "Operation identity")
        ]
