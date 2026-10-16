module ClaimCore.Tests.CliTransportFixture

open ClaimCore.ClientProtocol

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Cli
open ClaimCore.Contracts
open ClaimCore.ClientContract

let bytes (text: string) = Encoding.UTF8.GetBytes text

let frame endpoint =
    let definition = EndpointCatalog.tryFind endpoint |> Option.get

    let input =
        SchemaSamples.variants definition.Request
        |> List.head
        |> Encoding.UTF8.GetString

    bytes ($"{{\"endpoint\":\"{endpoint}\",\"input\":{input}}}")
    |> Frames.parse
    |> Result.defaultWith (fun _ -> failwith "Fixture frame refused.")

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
