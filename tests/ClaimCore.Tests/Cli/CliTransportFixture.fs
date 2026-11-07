module ClaimCore.Tests.CliTransportFixture

open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open ClaimCore.Cli
open ClaimCore.TestSupport

let bytes (text: string) = Encoding.UTF8.GetBytes text

let samples endpoint = ResponseExamples.samples endpoint

let frame endpoint =
    let route =
        Api.tryFind endpoint
        |> Option.defaultWith (fun () -> failtest "Known fixture endpoint")

    match route.Id with
    | EndpointId.Host HostEndpoint.Session -> Frames.create Api.session ()
    | EndpointId.Host HostEndpoint.Logout -> Frames.create Api.logout ()
    | EndpointId.Actor(ActorEndpoint.Operation _) ->
        let input =
            Inputs.caseOperation
                {
                    OperationId = CliReplyFixtures.id
                    CaseReference = "x"
                    ExpectedRevision = 0L
                    Act = AuthoredCaseAct.Open(Inputs.registrationOn "2026-01-01" "2026-01-01")
                }

        if endpoint = "operation.execute" then
            Frames.create Api.operationExecute input
        else
            Frames.create Api.operationPreview input
    | EndpointId.Actor(ActorEndpoint.Query query) ->
        CliExamples.queryArguments (CatalogOperation.Query query)
        |> Grammar.prepare (fun () -> CliReplyFixtures.id.ToString("D"))
        |> Result.map _.Invocation
        |> Result.mapError (fun errors ->
            {
                Problem.Path = "$"
                Reason = string errors
            })
    |> Result.defaultWith (fun problem -> failtestf "Typed fixture frame: %A" problem)

let authorityFrame () =
    ClaimCore.Json.JsonValue.Object
        [
            "endpoint", ClaimCore.Json.JsonValue.String "operation.execute"
            "input",
            Api.operationExecute.Input.Write(
                OperationInput.Authority CliReplyFixtures.authorityRequest
            )
        ]
    |> WireWriter.bytes
    |> Frames.admit
    |> Result.defaultWith (fun _ -> failwith "Authority fixture refused.")

let frameEndpoint invocation = Frames.endpoint invocation
let frameBytes invocation = Frames.encodeInvocation invocation

/// Only the declared operation echo follows the submitted request; server fields stay independent.
let echoedBody invocation bytes =
    let root =
        StrictJson.read Limits.MaximumResponseBytes bytes
        |> Result.defaultWith (fun _ -> failtest "Positive mock JSON")

    match Frames.operationId invocation, root with
    | Some operation, JsonValue.Object fields ->
        JsonValue.Object(
            ("operationId", JsonValue.String(operation.ToString("D")))
            :: (fields |> List.filter (fst >> (<>) "operationId"))
        )
        |> WireWriter.bytes
    | _ -> bytes

let result invocation body =
    StrictJson.read Limits.MaximumResponseBytes body
    |> Result.bind (fun reply ->
        ServiceReply.read (frameEndpoint invocation) reply
        |> Result.map ServiceReply.json)

let replyJson (encoded: byte array) =
    let encoded = encoded |> Array.takeWhile ((<>) (byte '\n'))

    Frames.decode encoded
    |> Result.defaultWith (fun _ -> failwith "Invalid reply.")
    |> ignore

    use document = JsonDocument.Parse(ReadOnlyMemory<byte> encoded)
    document.RootElement.Clone()

let response status body =
    let message = new HttpResponseMessage(enum<HttpStatusCode> status)
    let content = new ByteArrayContent(body)
    content.Headers.ContentType <- MediaTypeHeaderValue "application/json"
    message.Content <- content
    message

type Handler(send: HttpRequestMessage -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, _) = send request

let send handler frame =
    use client = new HttpClient(handler)
    client.Timeout <- TimeSpan.FromSeconds 5.

    RemoteServiceCall.send
        client
        {
            InstallationId = Some(System.Guid.Parse "90000000-0000-7000-8000-000000000001")
            Service = (Uri "https://localhost/")
            Authorization = (AuthenticationHeaderValue("Bearer", "synthetic-token"))
        }
        frame
        ignore
        CancellationToken.None
    |> _.GetAwaiter().GetResult()

let call (input: byte array) invoke =
    use source = new MemoryStream(input)
    use output = new MemoryStream()
    use errors = new MemoryStream()

    use processor =
        RemoteFrameProcessor.ForTest(source, output, errors, invoke, (fun _ -> Task.FromResult()))

    let code = processor.Call CancellationToken.None
    code, output.ToArray(), errors.ToArray()
