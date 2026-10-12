module ClaimCore.WebTests.AdmissionDeadlineTests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ClaimCore.Contracts
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures

/// Token-bearing native stream, deliberately stalled on controlled synthetic input.
type private StalledStream() =
    inherit MemoryStream()

    override _.ReadAsync(_, _, _, token) =
        task {
            do! Task.Delay(Timeout.Infinite, token)
            return 0
        }

let private callerCancellation () =
    use stream = new StalledStream()
    use caller = new CancellationTokenSource()
    let read = HttpBody.readBounded 16 stream caller.Token
    caller.Cancel()

    Expect.equal
        (read.WaitAsync(TimeSpan.FromSeconds 1.).Result)
        (Error HttpInputProblem.BodyCancelled)
        "Caller owns the native I/O cancellation."

let private deadline () =
    use stream = new StalledStream()
    let read = HttpBody.readBounded 16 stream CancellationToken.None

    Expect.equal
        (read.WaitAsync(TimeSpan.FromSeconds 12.).Result)
        (Error HttpInputProblem.BodyCancelled)
        "Absolute ten-second acquisition deadline releases stalled input."

let private cancelledAfterRead () =
    use cancelled = new CancellationTokenSource()
    let request = context validGet
    request.RequestAborted <- cancelled.Token
    let core = RuntimeStub()

    let decode bytes =
        let result = HttpInput.caseReference bytes
        cancelled.Cancel()
        result

    let invoke reference =
        core.ActorCore.Get(reference, CancellationToken.None)

    Routes.json admit 65536 decode invoke WebWire.get request
    |> readResult
    |> ignore

    Expect.equal core.CoreCalls 0 "Cancellation that wins after decode prevents dispatch."

let private hostStopping () =
    use stopping = new CancellationTokenSource()
    use stream = new StalledStream()
    let services = ServiceCollection()

    services.AddSingleton<IHostApplicationLifetime>(
        { new IHostApplicationLifetime with
            member _.ApplicationStarted = CancellationToken.None
            member _.ApplicationStopping = stopping.Token
            member _.ApplicationStopped = CancellationToken.None
            member _.StopApplication() = stopping.Cancel()
        }
    )
    |> ignore

    use provider = services.BuildServiceProvider()
    let request = context validGet
    request.RequestServices <- provider
    request.Request.Body <- stream
    let read = HttpBody.readRequest 65536 request
    stopping.Cancel()

    Expect.equal
        (read.WaitAsync(TimeSpan.FromSeconds 1.).GetAwaiter().GetResult())
        (Error HttpInputProblem.BodyCancelled)
        "Host stop cancels owned acquisition without a body or core dispatch."

let tests =
    testList
        "body acquisition ownership"
        [
            testCase "[CC-WEB-001] caller cancels stalled input before dispatch" callerCancellation
            testCase "[CC-WEB-001] stalled input has an absolute body deadline" deadline
            testCase "[CC-WEB-001] host stop cancels active body acquisition" hostStopping
            testCase
                "[CC-WEB-001] cancellation after decode prevents a core call"
                cancelledAfterRead
        ]
