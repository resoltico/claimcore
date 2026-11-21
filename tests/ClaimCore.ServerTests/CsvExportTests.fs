module ClaimCore.ServerTests.CsvExportTests

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Json
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Server

let private bytes (value: string) = Encoding.UTF8.GetBytes value

let private page records count next =
    Ok
        {
            ExportPage.Rows = records |> Seq.map bytes
            ItemCount = count
            CaseId = None
            NextCursor = next
        }

let private run maximum pages =
    let mutable requests = []
    let mutable remaining = pages

    let traversal =
        {
            ExportTraversal.Header = bytes "header\r\n"
            Read =
                fun cursor _ ->
                    requests <- cursor :: requests

                    match remaining with
                    | value :: rest ->
                        remaining <- rest
                        Task.FromResult value
                    | [] -> failtest "Unexpected export page acquisition"
        }

    task {
        let! result = CsvExport.materialize maximum CancellationToken.None traversal
        return result, List.rev requests
    }

[<Tests>]
let tests =
    testList
        "bounded CSV materialization"
        [
            testCaseAsync
                "empty authorized selection is header-only and starts without a cursor"
                (async {
                    let! result, requests = run 100 [ page [] 0 None ] |> Async.AwaitTask

                    Expect.equal
                        result
                        (Ok
                            {
                                Bytes = bytes "header\r\n"
                                RowCount = 0
                                CaseId = None
                            })
                        "Complete header-only file with zero data rows"

                    Expect.equal requests [ None ] "Fresh traversal"
                })
            testCaseAsync
                "pages retain order and count encoded expansion rather than source items"
                (async {
                    let! result, requests =
                        run
                            100
                            [
                                page [ "a,b\r\n" ] 1 (Some "next")
                                page [ "c,d\r\n"; "e,f\r\n" ] 1 None
                            ]
                        |> Async.AwaitTask

                    Expect.equal
                        result
                        (Ok
                            {
                                Bytes = bytes "header\r\na,b\r\nc,d\r\ne,f\r\n"
                                RowCount = 3
                                CaseId = None
                            })
                        "All expanded rows counted independently of source item count"

                    Expect.equal requests [ None; Some "next" ] "Continuation from core only"
                })
            testCaseAsync
                "page-two refusal never returns the first page as a successful subset"
                (async {
                    let failure = ExportFailure.Failed CoreFault.StoreUnavailable

                    let! result, _ =
                        run 100 [ page [ "first\r\n" ] 1 (Some "next"); Error failure ]
                        |> Async.AwaitTask

                    Expect.equal result (Error failure) "No successful partial document"
                })
            testCaseAsync
                "exact shared byte ceiling succeeds and the next encoded byte refuses"
                (async {
                    let header = bytes "header\r\n"

                    let document count =
                        {
                            ExportTraversal.Header = header
                            Read =
                                fun _ _ ->
                                    Task.FromResult(
                                        Ok
                                            {
                                                ExportPage.Rows =
                                                    Seq.singleton (Array.create count (byte 'x'))
                                                ItemCount = 1
                                                CaseId = None
                                                NextCursor = None
                                            }
                                    )
                        }

                    let! exact =
                        CsvExport.materialize
                            Limits.MaximumResponseBytes
                            CancellationToken.None
                            (document (Limits.MaximumResponseBytes - header.Length))
                        |> Async.AwaitTask

                    match exact with
                    | Ok value ->
                        Expect.equal
                            value.Bytes.Length
                            Limits.MaximumResponseBytes
                            "Exact encoded ceiling"
                    | Error value -> failtestf "%A" value

                    let! overflow =
                        CsvExport.materialize
                            Limits.MaximumResponseBytes
                            CancellationToken.None
                            (document (Limits.MaximumResponseBytes - header.Length + 1))
                        |> Async.AwaitTask

                    Expect.equal
                        overflow
                        (Error(ExportFailure.LimitExceeded ExportAdvice.limitExceeded))
                        "Expanded source item is bounded by bytes"
                })
            testCaseAsync
                "repeated nonadvancing and empty continuations refuse without an endless traversal"
                (async {
                    for pages in
                        [
                            [ page [ "a" ] 1 (Some "x"); page [ "b" ] 1 (Some "x") ]
                            [
                                page [ "a" ] 1 (Some "x")
                                page [ "b" ] 1 (Some "y")
                                page [ "c" ] 1 (Some "x")
                            ]
                            [ page [] 0 (Some "x") ]
                            [ page [] 1 (Some "x") ]
                        ] do
                        let! result, _ = run 100 pages |> Async.AwaitTask

                        Expect.equal
                            result
                            (Error(
                                ExportFailure.InvalidContinuation ExportAdvice.invalidContinuation
                            ))
                            "Impossible continuation is not completion"
                })
            testCaseAsync
                "encoding stops at the byte ceiling and disposes the record source"
                (async {
                    let mutable encoded = 0
                    let mutable disposed = false

                    let chunks =
                        seq {
                            use _owned =
                                { new IDisposable with
                                    member _.Dispose() = disposed <- true
                                }

                            for _ in 1..3 do
                                encoded <- encoded + 1
                                yield bytes "record\r\n"
                        }

                    let traversal =
                        {
                            ExportTraversal.Header = bytes "h\r\n"
                            Read =
                                fun _ _ ->
                                    Task.FromResult(
                                        Ok
                                            {
                                                ExportPage.Rows = chunks
                                                ItemCount = 1
                                                CaseId = None
                                                NextCursor = None
                                            }
                                    )
                        }

                    let! result =
                        CsvExport.materialize 11 CancellationToken.None traversal |> Async.AwaitTask

                    Expect.equal
                        result
                        (Error(ExportFailure.LimitExceeded ExportAdvice.limitExceeded))
                        "No oversized success"

                    Expect.equal encoded 2 "No encoding after the first nonfitting record"
                    Expect.isTrue disposed "Record enumeration cleanup completed"
                })
            testCaseAsync
                "cancellation waits for provider cleanup instead of completing the export early"
                (async {
                    use cancellation = new CancellationTokenSource()

                    let started =
                        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

                    let cleanup =
                        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

                    let traversal =
                        {
                            ExportTraversal.Header = bytes "header\r\n"
                            Read =
                                fun _ token ->
                                    task {
                                        started.SetResult()

                                        try
                                            do! Task.Delay(Timeout.Infinite, token)
                                            return failtest "Cancellation was not observed"
                                        with :? OperationCanceledException as error ->
                                            do! cleanup.Task
                                            return raise error
                                    }
                        }

                    let pending = CsvExport.materialize 100 cancellation.Token traversal
                    do! started.Task |> Async.AwaitTask
                    cancellation.Cancel()
                    Expect.isFalse pending.IsCompleted "Actual cleanup still owns the operation"
                    cleanup.SetResult()
                    let mutable cancelled = false

                    try
                        let! _ = pending |> Async.AwaitTask in ()
                    with :? OperationCanceledException ->
                        cancelled <- true

                    Expect.isTrue cancelled "No successful cancelled file"
                })
        ]

let private installation = Guid.Parse "10000000-0000-4000-8000-000000000001"

let private calendar =
    { new ClaimCore.Application.IBusinessCalendar with
        member _.TimeZoneId = "Etc/UTC"

        member _.DateAt instant =
            DateOnly.FromDateTime instant.UtcDateTime
    }

let private requestBody =
    Encoding.UTF8.GetBytes """{"query":"case.list","selection":{}}"""

let private cancellationLease kind =
    task {
        use requestCancellation = new CancellationTokenSource()

        let started =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let cancelled =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let cleanup =
            TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

        let store =
            { new ClaimCore.Application.Storage.IStore with
                member _.RecordDisclosure(_, _) =
                    Task.FromResult(Error ClaimCore.Application.Storage.StoreFailure.Unavailable)

                member _.Context _ =
                    Task.FromResult(Error ClaimCore.Application.Storage.StoreFailure.Unavailable)

                member _.Write _ =
                    Task.FromResult(Error ClaimCore.Application.Storage.StoreFailure.Unavailable)

                member _.Read operation =
                    task {
                        started.TrySetResult() |> ignore

                        use _registration =
                            operation.CancellationToken.Register(fun () ->
                                cancelled.TrySetResult() |> ignore)

                        try
                            do! Task.Delay(Timeout.Infinite, operation.CancellationToken)
                        with :? OperationCanceledException ->
                            ()

                        do! cleanup.Task
                        return Error ClaimCore.Application.Storage.StoreFailure.Cancelled
                    }
            }

        use host =
            new ClaimCore.TestSupport.TestServerFixture.Host(
                true,
                installationId = installation,
                forActor =
                    (fun principal ->
                        ClaimCore.Application.Service.create
                            "synthetic"
                            installation
                            store
                            calendar
                            principal)
            )

        let! _ = host.Login()
        let! token = host.Token()

        let pending =
            host.CallResponse(
                ExportTransport.token,
                requestBody,
                token,
                cancellationToken = requestCancellation.Token
            )

        try
            do! started.Task.WaitAsync(TimeSpan.FromSeconds 5.)

            let capacity =
                Microsoft.Extensions.DependencyInjection
                    .ServiceProviderServiceExtensions.GetRequiredService<PrincipalAdmission>(
                    host.Services
                )

            Expect.equal capacity.ActiveReads 1L "One outer read lease; no page lease reacquisition"

            match kind with
            | "request" -> requestCancellation.Cancel()
            | "shutdown" ->
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                    .GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(
                        host.Services
                    )
                    .StopApplication()
            | "deadline" -> ()
            | _ -> failtest "Unknown cancellation witness"

            do! cancelled.Task.WaitAsync(TimeSpan.FromSeconds 20.)

            Expect.equal
                capacity.ActiveReads
                1L
                "Cancellation does not release capacity before provider cleanup"

            cleanup.TrySetResult() |> ignore

            try
                use! response = pending
                let! payload = response.Content.ReadAsByteArrayAsync()

                Expect.notEqual
                    (nonNull response.Content.Headers.ContentType).MediaType
                    "text/csv"
                    "No successful CSV after cancellation"

                if kind = "deadline" then
                    Expect.stringContains
                        (Encoding.UTF8.GetString payload)
                        "PROCESSING_TIMEOUT"
                        "Finite processing deadline has a typed failure"
            with :? OperationCanceledException ->
                ()

            let until = Diagnostics.Stopwatch.StartNew()

            while capacity.ActiveReads <> 0L && until.Elapsed < TimeSpan.FromSeconds 5. do
                do! Task.Delay 5

            Expect.equal
                capacity.ActiveReads
                0L
                "Actual cleanup and response lifetime release capacity"
        finally
            cleanup.TrySetResult() |> ignore
            requestCancellation.Cancel()

            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(
                    host.Services
                )
                .StopApplication()
    }

[<Tests>]
let cancellationTests =
    testList
        "CSV export request lifetime"
        [
            testCaseAsync
                "request abort retains one read lease through provider cleanup"
                (async { do! cancellationLease "request" |> Async.AwaitTask })
            testCaseAsync
                "host stop retains one read lease through provider cleanup"
                (async { do! cancellationLease "shutdown" |> Async.AwaitTask })
            testCaseAsync
                "finite processing timeout retains one read lease through provider cleanup"
                (async { do! cancellationLease "deadline" |> Async.AwaitTask })
            testCaseAsync
                "actor transport gates apply before core export and failures stay JSON"
                (async {
                    let unavailableStore =
                        { new ClaimCore.Application.Storage.IStore with
                            member _.RecordDisclosure(_, _) =
                                Task.FromResult(
                                    Error ClaimCore.Application.Storage.StoreFailure.Unavailable
                                )

                            member _.Context _ =
                                Task.FromResult(
                                    Error ClaimCore.Application.Storage.StoreFailure.Unavailable
                                )

                            member _.Read _ =
                                Task.FromResult(
                                    Error ClaimCore.Application.Storage.StoreFailure.Unavailable
                                )

                            member _.Write _ =
                                Task.FromResult(
                                    Error ClaimCore.Application.Storage.StoreFailure.Unavailable
                                )
                        }

                    use host =
                        new ClaimCore.TestSupport.TestServerFixture.Host(
                            true,
                            installationId = installation,
                            forActor =
                                (fun principal ->
                                    ClaimCore.Application.Service.create
                                        "synthetic"
                                        installation
                                        unavailableStore
                                        calendar
                                        principal)
                        )

                    let! _ = host.Login() |> Async.AwaitTask
                    let! token = host.Token() |> Async.AwaitTask

                    let! contract, _ =
                        host.Call(
                            ExportTransport.token,
                            requestBody,
                            token,
                            fingerprint = String.replicate 64 "0"
                        )
                        |> Async.AwaitTask

                    Expect.equal contract 409 "Contract handshake"

                    let! csrf, _ =
                        host.Call(ExportTransport.token, requestBody, "wrong") |> Async.AwaitTask

                    Expect.equal csrf 403 "Antiforgery"

                    let! missing, _ =
                        host.Send(
                            Net.Http.HttpMethod.Post,
                            ExportTransport.path,
                            Some requestBody,
                            [
                                "Origin",
                                ClaimCore.TestSupport.TestServerFixture.origin.GetLeftPart(
                                    UriPartial.Authority
                                )
                                "ClaimCore-Contract", ContractIdentity.value
                                "X-ClaimCore-Antiforgery", token
                            ]
                        )
                        |> Async.AwaitTask

                    Expect.equal missing 400 "Required installation binding"

                    use! unavailable =
                        host.CallResponse(ExportTransport.token, requestBody, token)
                        |> Async.AwaitTask

                    let! payload = unavailable.Content.ReadAsByteArrayAsync() |> Async.AwaitTask

                    Expect.equal
                        (nonNull unavailable.Content.Headers.ContentType).MediaType
                        "application/json"
                        "No JSON writer on success and no CSV on core failure"

                    Expect.isOk
                        (Codec.decode Limits.MaximumResponseBytes ExportTransport.failure payload)
                        "Typed JSON failure"

                    Expect.isTrue
                        ((string unavailable.Headers.CacheControl).Contains "no-store")
                        "Private failure"

                    Expect.stringContains
                        host.Log
                        "\"endpoint\":\"export\""
                        "Known finite diagnostic endpoint"
                })
        ]
