module ClaimCore.Tests.CliTransportFixture

open ClaimCore.Contract
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

let bytes (text: string) = Encoding.UTF8.GetBytes text

let samples endpoint = CliReplyFixtures.responses endpoint

let frame endpoint =
    ClaimCore.Json.JsonValue.Object
        [
            "endpoint", ClaimCore.Json.JsonValue.String endpoint
            "input", CliReplyFixtures.input endpoint
        ]
    |> WireWriter.bytes
    |> Frames.admit
    |> Result.defaultWith (fun _ -> failwith "Fixture frame refused.")

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

let result invocation body =
    StrictJson.read Limits.MaximumResponseBytes body
    |> Result.bind (fun reply ->
        Api.admitOutput (frameEndpoint invocation) reply
        |> Result.bind (fun () ->
            if Correlation.matches (frameEndpoint invocation) (Frames.input invocation) reply then
                Ok reply
            else
                Error { Path = "$"; Reason = "CORRELATION" }))

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
        (Uri "https://localhost/")
        frame
        (AuthenticationHeaderValue("Bearer", "synthetic-token"))
        ignore
        CancellationToken.None
    |> _.GetAwaiter().GetResult()

let call (input: byte array) invoke =
    use source = new MemoryStream(input)
    use output = new MemoryStream()
    use errors = new MemoryStream()
    use processor = RemoteFrameProcessor.ForTest(source, output, errors, invoke)
    let code = processor.Call CancellationToken.None
    code, output.ToArray(), errors.ToArray()
