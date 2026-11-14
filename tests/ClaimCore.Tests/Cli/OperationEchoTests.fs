module ClaimCore.Tests.OperationEchoTests

open ClaimCore.CommandLine

open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private delivered endpoint input reply =
    let frame = Frames.create endpoint input |> Result.defaultWith (string >> failtest)
    use response = new HttpResponseMessage(HttpStatusCode.OK)

    response.Content <-
        new ByteArrayContent(Codec.encode endpoint.Output (RepresentationReply.Data reply))

    response.Content.Headers.ContentType <- MediaTypeHeaderValue "application/json"

    RemoteServiceCall.readResponse frame response CancellationToken.None
    |> fun pending -> pending.GetAwaiter().GetResult()

[<Tests>]
let tests =
    testCase "typed operation ID echo alone controls reply delivery certainty" (fun () ->
        let operation = OperationIds.fresh ()
        let different = OperationIds.fresh ()

        let input =
            Inputs.caseOperation
                {
                    OperationId = operation
                    CaseReference = "ECHO-1"
                    ExpectedRevision = 0L
                    Act = AuthoredCaseAct.Open Inputs.registration
                }

        for echoed, expected in
            [ operation, DeliveryState.Delivered; different, DeliveryState.Uncertain ] do
            let executed =
                delivered
                    Api.operationExecute
                    (Submissions.ofCommand None input)
                    {
                        ExecuteReply.OperationId = echoed
                        Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
                    }

            Expect.equal
                executed.Frame.Delivery
                expected
                "Execute echoes are checked after typed decoding"

            if echoed = different then
                Expect.equal
                    executed.ExitCode
                    4
                    "A mismatched execute echo leaves delivery uncertain"

            let previewed =
                delivered
                    Api.operationPreview
                    (Submissions.ofCommand None input)
                    {
                        PreviewReply.OperationId = echoed
                        Outcome = Outcome.Failed CoreFault.StoreUnavailable
                    }

            Expect.equal
                previewed.Frame.Delivery
                expected
                "Preview echoes are checked once at transport admission"

            let lookedUp =
                delivered
                    Api.operationGet
                    {
                        OperationLookup.OperationId = operation
                    }
                    {
                        OperationReply.OperationId = echoed
                        Outcome =
                            Outcome.Succeeded(
                                OperationLookupResult.Pending(
                                    OperationLifetime.finalAt (CaseFixtures.operationId operation)
                                )
                            )
                    }

            Expect.equal
                lookedUp.Frame.Delivery
                expected
                "Pending lookups retain the typed operation echo")
