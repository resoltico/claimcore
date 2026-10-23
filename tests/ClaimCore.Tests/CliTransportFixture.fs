module ClaimCore.Tests.CliTransportFixture

open ClaimCore.Contract
open ClaimCore.Contract.Protocol

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

let samples file endpoint =
    let root = ClaimCore.TestSupport.RepositoryRoot.find ()

    use document =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "tests", "fixtures", file)))

    document.RootElement.GetProperty("cases").EnumerateArray()
    |> Seq.filter (fun item -> item.GetProperty("endpoint").GetString() = endpoint)
    |> Seq.map (fun item -> item.GetProperty("value").GetRawText() |> bytes)
    |> Seq.toList

let frame endpoint =
    let input =
        match endpoint with
        | "session"
        | "session.logout"
        | "context" -> bytes "{}"
        | _ -> samples "wire-request-examples.json" endpoint |> List.head

    use document = JsonDocument.Parse(ReadOnlyMemory<byte> input)
    let input = JsonSerializer.SerializeToUtf8Bytes document.RootElement

    bytes ($"{{\"endpoint\":\"{endpoint}\",\"input\":{Encoding.UTF8.GetString input}}}")
    |> Frames.admit
    |> Result.defaultWith (fun _ -> failwith "Fixture frame refused.")

let frameEndpoint invocation =
    Frames.request invocation |> WireRequest.endpoint

let frameBytes invocation =
    bytes (
        $"{{\"endpoint\":\"{Endpoint.id (frameEndpoint invocation)}\",\"input\":{Encoding.UTF8.GetString(Frames.inputBytes invocation)}}}"
    )

let result invocation body =
    Replies.decode (frameEndpoint invocation) body
    |> Result.bind (fun reply ->
        if Correlation.matches (Frames.request invocation) reply then
            Ok reply
        else
            Error { Path = "$"; Reason = "CORRELATION" })

let replyJson encoded =
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
