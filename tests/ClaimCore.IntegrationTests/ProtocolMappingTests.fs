module ClaimCore.IntegrationTests.ProtocolMappingTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.Web.Composition
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Contract.Protocol
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures

let private admitted result =
    result
    |> Result.defaultWith (fun _ -> failtest "Expected admitted synthetic request")

let private applicationExamples () =
    use document =
        JsonDocument.Parse(
            File
                .ReadAllBytes(
                    Path.Combine(
                        RepositoryRoot.find (),
                        "tests/fixtures/wire-request-examples.json"
                    )
                )
                .AsMemory()
        )

    document.RootElement.GetProperty("cases").EnumerateArray()
    |> Seq.filter (fun example -> example.GetProperty("valid").GetBoolean())
    |> Seq.map (fun example ->
        let endpoint =
            example.GetProperty("endpoint").GetString()
            |> nonNull
            |> Api.tryFind
            |> Option.get

        let input =
            example
                .GetProperty("value")
                .GetRawText()
                .Replace("\"issuer\": \"x\"", "\"issuer\": \"https://issuer.example/\"")
            |> Encoding.UTF8.GetBytes
            |> StrictJson.read Limits.MaximumRequestBytes
            |> admitted

        endpoint, input)
    |> Seq.filter (fun (endpoint, _) ->
        Api.id endpoint <> "session" && Api.id endpoint <> "session.logout")
    |> Seq.toList

let private mappedExamples () =
    async {
        let! _, fixture = fixture () |> Async.AwaitTask

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! _ = caseWorker fixture |> Async.AwaitTask
        let actor = core fixture owner

        for endpoint, input in applicationExamples () do
            let actorEndpoint =
                match Api.identity endpoint with
                | EndpointId.Actor actor -> actor
                | _ -> failtest "Expected an actor endpoint"

            let prepared = (Handlers.route actorEndpoint).Prepare input |> admitted
            let! reply = prepared (Some actor) CancellationToken.None ignore |> Async.AwaitTask
            Api.admitOutput endpoint reply |> admitted |> ignore

            Expect.isTrue
                (ClaimCore.Contract.Correlation.matches endpoint input reply)
                "Application replies preserve typed request correlation."
    }

let private cancellationPolicy () =
    async {
        let! _, fixture = fixture () |> Async.AwaitTask

        use _owned =
            { new System.IDisposable with
                member _.Dispose() = fixture.Dispose()
            }

        let! _ = caseWorker fixture |> Async.AwaitTask
        let actor = core fixture owner
        let input = OperationInput.Case(openRequest 91 "MAPPING-001")
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let! preview =
            (Core.previewReply actor (input) (cancellation.Token)) |> Async.AwaitTask

        Expect.equal
            preview.Outcome
            (PreviewResult.Failed CoreFault.StoreUnavailable)
            "Preview observes caller cancellation."

        let! execute =
            (Core.execute actor (input) (CancellationToken.None)) |> Async.AwaitTask

        match execute.Outcome with
        | ExecuteResult.Accepted _ -> ()
        | _ -> failtest "An admitted mutation completes with the noncancelled dispatch token."

        Api.operationExecute.Output.Write execute
        |> Api.admitOutput (Api.tryFind "operation.execute" |> Option.get)
        |> admitted
        |> ignore
    }

[<Tests>]
let tests =
    testList
        "typed protocol mapping"
        [
            testCaseAsync
                "all published request examples cross Application and return correlated typed replies"
                (mappedExamples ())
            testCaseAsync
                "preview cancellation and an uncancelled mutation retain their completion paths"
                (cancellationPolicy ())
        ]
