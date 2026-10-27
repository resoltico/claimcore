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
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json
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

// This is a server invariant, asserted independently from any client admission helper.
let private assertServerIdentity endpoint input reply =
    let memberValue name value =
        JsonValue.tryMember name value
        |> Option.defaultWith (fun () -> failtestf "Missing identity member %s" name)

    let guid name value =
        match memberValue name value with
        | JsonValue.String text -> Guid.Parse text
        | _ -> failtest "Expected a GUID identity"

    let outcome = memberValue "outcome" reply
    let kind = memberValue "kind" outcome

    let receiptIdentity expected =
        let receipt = memberValue "data" outcome

        Expect.equal
            (guid "operationId" receipt)
            expected
            "Receipt retains the requested operation id"

    match Api.identity endpoint with
    | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Preview)
    | EndpointId.Actor(ActorEndpoint.Operation OperationEndpoint.Execute) ->
        let expected = guid "operationId" input

        Expect.equal
            (guid "operationId" reply)
            expected
            "Reply echoes the requested operation id as data"

        if kind = JsonValue.String "ACCEPTED" || kind = JsonValue.String "ALREADY_ACCEPTED" then
            receiptIdentity expected
    | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Observe) when
        kind = JsonValue.String "SUCCEEDED"
        ->
        receiptIdentity (guid "operationId" input)
    | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.Resolve) when
        kind = JsonValue.String "ACCEPTED"
        ->
        receiptIdentity (guid "operationId" input)
    | EndpointId.Actor(ActorEndpoint.Query QueryEndpoint.AuthorityGrants) when
        kind = JsonValue.String "SUCCEEDED"
        ->
        let actor = memberValue "data" outcome |> memberValue "actor"

        Expect.equal
            (guid "actorId" actor)
            (guid "actorId" input)
            "Grant page retains its requested actor"
    | _ -> ()

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

            let prepared = (ApiRoutes.route actorEndpoint).Prepare input |> admitted

            let! reply =
                prepared.Invoke (Some actor) CancellationToken.None ignore |> Async.AwaitTask

            Api.admitOutput endpoint reply |> admitted |> ignore

            assertServerIdentity endpoint input reply
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
            (Service.preview actor (input) (cancellation.Token)) |> Async.AwaitTask

        Expect.equal
            preview.Outcome
            (PreviewResult.Failed CoreFault.StoreUnavailable)
            "Preview observes caller cancellation."

        let! execute =
            (Service.execute actor (input) (CancellationToken.None)) |> Async.AwaitTask

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
                "all published request examples cross the service and preserve server identity"
                (mappedExamples ())
            testCaseAsync
                "preview cancellation and an uncancelled mutation retain their completion paths"
                (cancellationPolicy ())
        ]
