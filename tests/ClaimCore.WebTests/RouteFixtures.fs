module ClaimCore.WebTests.RouteFixtures

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

let operationId = RouteFixtureRuntime.operationId
type RuntimeStub = RouteFixtureRuntime.RuntimeStub

let context content =
    let value = DefaultHttpContext()
    value.Request.Body <- new MemoryStream(Encoding.UTF8.GetBytes(content: string))
    value.Response.Body <- new MemoryStream()
    value

type FaultingWriteStream() =
    inherit MemoryStream()

    override _.Write(_: byte array, _: int, _: int) =
        raise (IOException("Synthetic response delivery failure."))

    override _.WriteAsync(_: byte array, _: int, _: int, _: CancellationToken) =
        Task.FromException(IOException("Synthetic response delivery failure."))

    override _.WriteAsync(_: ReadOnlyMemory<byte>, _: CancellationToken) =
        ValueTask(Task.FromException(IOException("Synthetic response delivery failure.")))

let admit _ = Task.FromResult(Ok())

let execute (context: HttpContext) (result: IResult) =
    let services = ServiceCollection()
    services.AddOptions() |> ignore
    services.AddLogging() |> ignore
    context.RequestServices <- services.BuildServiceProvider()
    result.ExecuteAsync(context).GetAwaiter().GetResult()
    Encoding.UTF8.GetString((context.Response.Body :?> MemoryStream).ToArray())

let readResult (operation: Task<IResult>) = operation.GetAwaiter().GetResult()

let validGet = """{"caseReference":"WEB-V2-001"}"""
let validList = """{"limit":10}"""

let validResolve =
    $"""{{"operationId":"{operationId:D}","requestSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}"""
