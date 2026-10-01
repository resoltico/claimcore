module ClaimCore.WebTests.AsyncTransportTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Contracts
open ClaimCore.Web

type private GatedStream(bytes: byte array, failure: exn option) =
    inherit Stream()
    let inner = new MemoryStream(bytes)

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    member _.Release() = release.SetResult(())
    override _.CanRead = true
    override _.CanWrite = false
    override _.CanSeek = false
    override _.Length = inner.Length

    override _.Position
        with get () = inner.Position
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Read(buffer, offset, count) = inner.Read(buffer, offset, count)
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())

    override _.ReadAsync(buffer, offset, count, cancelled) =
        task {
            do! release.Task

            match failure with
            | Some error -> return raise error
            | None -> return! inner.ReadAsync(buffer, offset, count, cancelled)
        }

    override _.Dispose(disposing) =
        if disposing then
            inner.Dispose()

        base.Dispose(disposing)

let private bounded label bytes failure expected =
    testCase label (fun () ->
        use stream = new GatedStream(bytes, failure)
        let pending = HttpBody.readBounded 3 stream
        Expect.isFalse pending.IsCompleted "Network read genuinely waits for data"
        stream.Release()

        Expect.isTrue
            (pending.GetAwaiter().GetResult() = expected)
            "Same allocation and failure rules after asynchronous suspension")

let private dispatchFailure phase expected =
    testCase
        ($"[CC-WEB-001] suspended dispatch phase {phase} preserves failure knowledge")
        (fun () ->
            let context = DefaultHttpContext()
            use output = new MemoryStream()
            context.Response.Body <- output
            let services = ServiceCollection()
            services.AddOptions() |> ignore
            services.AddLogging() |> ignore
            use provider = services.BuildServiceProvider()
            context.RequestServices <- provider

            let release =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let next =
                RequestDelegate(fun request ->
                    task {
                        if phase > 0 then
                            RouteSupport.markDispatched request

                        if phase > 1 then
                            RouteSupport.markCompleted request

                        do! release.Task
                        return raise (IOException("SYNTHETIC-PRIVATE-DETAIL"))
                    }
                    :> Task)

            let pending = RouteSupport.handleFailures context next
            Expect.isFalse pending.IsCompleted "Dispatch is not yet observed"
            release.SetResult(())
            pending.GetAwaiter().GetResult()
            let response = Encoding.UTF8.GetString(output.ToArray())

            Expect.isFalse
                (response.Contains("SYNTHETIC-PRIVATE-DETAIL", StringComparison.Ordinal))
                "Provider details remain private"

            use document = JsonDocument.Parse(response)

            Expect.equal
                (document.RootElement.GetProperty("diagnostic").GetProperty("id").GetString())
                expected
                "Exact dispatch knowledge after a delayed failure")

let tests =
    testList
        "asynchronous HTTP transport boundaries"
        [
            bounded
                "[CC-WEB-001] suspended exact-size request is accepted"
                [| 1uy; 2uy; 3uy |]
                None
                (Ok [| 1uy; 2uy; 3uy |])
            bounded
                "[CC-WEB-001] suspended oversized request is refused"
                [| 1uy; 2uy; 3uy; 4uy |]
                None
                (Error HttpInputProblem.BodyTooLarge)
            bounded
                "[CC-WEB-001] suspended I/O failure retains safe refusal"
                [||]
                (Some(IOException("SYNTHETIC-PRIVATE-DETAIL")))
                (Error HttpInputProblem.BodyUnreadable)
            bounded
                "[CC-WEB-001] suspended cancellation retains safe refusal"
                [||]
                (Some(OperationCanceledException()))
                (Error HttpInputProblem.BodyCancelled)
            bounded
                "[CC-WEB-001] suspended Kestrel size refusal retains transport classification"
                [||]
                (Some(BadHttpRequestException("SYNTHETIC-PRIVATE-DETAIL", 413)))
                (Error HttpInputProblem.BodyTooLarge)
            dispatchFailure 0 "WEB_HOST_BEFORE_DISPATCH_FAILED"
            dispatchFailure 1 "WEB_HOST_DISPATCH_UNCONFIRMED"
            dispatchFailure 2 "WEB_HOST_COMPLETED_RESPONSE_FAILED"
        ]
