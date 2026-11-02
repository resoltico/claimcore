module ClaimCore.IntegrationTests.HttpAdmissionPressureTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Protocol
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.TestSupport.TestServerFixture
open ClaimCore.Web

type private SlowBody() =
    inherit HttpContent()

    let entered =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    do base.Headers.ContentType <- MediaTypeHeaderValue "application/json"
    member _.Entered = entered.Task
    member _.Release() = release.TrySetResult() |> ignore

    override _.TryComputeLength(length) =
        length <- 2L
        true

    override _.SerializeToStreamAsync(stream, _) =
        task {
            do! stream.WriteAsync(ReadOnlyMemory(Encoding.UTF8.GetBytes "{")).AsTask()
            entered.TrySetResult() |> ignore
            do! release.Task
            do! stream.WriteAsync(ReadOnlyMemory(Encoding.UTF8.GetBytes "}")).AsTask()
        }
        :> Task

type private StoreProbe(storage: IStore) =
    let tokens = ConcurrentBag<CancellationToken>()
    let mutable ignoreCancellation = false

    member _.IgnoreCancellation
        with set value = ignoreCancellation <- value

    member _.ObservedReads = tokens.Count
    member _.CancelledReads = tokens |> Seq.filter _.IsCancellationRequested |> Seq.length

    interface IStore with
        member _.Read operation =
            if ignoreCancellation then
                tokens.Add operation.CancellationToken

                storage.Read
                    { operation with
                        CancellationToken = CancellationToken.None
                    }
            else
                storage.Read operation

        member _.Write operation = storage.Write operation

type private Cookie = { Value: string; Token: string }

let private cookies (response: HttpResponseMessage) =
    match response.Headers.TryGetValues "Set-Cookie" with
    | true, values ->
        (nonNull values)
        |> Seq.map (fun value -> value.Split(';')[0])
        |> String.concat "; "
    | _ -> ""

let private login (host: Host) (subject: string) =
    task {
        use! response =
            host.Client.PostAsync(
                "/__test/login?subject=" + Uri.EscapeDataString subject,
                new ByteArrayContent(Array.empty)
            )

        Expect.equal (int response.StatusCode) 200 "Independent synthetic cookie login"
        let initial = cookies response
        use request = new HttpRequestMessage(HttpMethod.Get, "/api/session")
        request.Headers.Add("Cookie", initial)
        request.Headers.Add("X-Synthetic-Caller", Guid.NewGuid().ToString("D"))
        request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)
        use! bootstrap = host.Client.SendAsync request
        Expect.equal (int bootstrap.StatusCode) 200 "Independent antiforgery bootstrap"
        let! body = bootstrap.Content.ReadAsByteArrayAsync()
        use parsed = JsonDocument.Parse body

        return
            {
                Value = initial + "; " + cookies bootstrap
                Token =
                    parsed.RootElement
                        .GetProperty("outcome")
                        .GetProperty("data")
                        .GetProperty("antiforgeryToken")
                        .GetString()
                    |> nonNull
            }
    }

let private send
    (host: Host)
    (cookie: Cookie)
    (installation: Guid)
    (endpoint: string)
    (body: HttpContent)
    (cancellation: CancellationToken)
    : Task<int * byte array> =
    task {
        use request = new HttpRequestMessage(HttpMethod.Post, "/api/" + endpoint)
        request.Headers.Add("Cookie", cookie.Value)
        request.Headers.Add("X-Synthetic-Caller", Guid.NewGuid().ToString("D"))
        request.Headers.Add("ClaimCore-Contract", ContractIdentity.value)
        request.Headers.Add("ClaimCore-Installation", string installation)
        request.Headers.Add("Origin", origin.AbsoluteUri.TrimEnd('/'))
        request.Headers.Add("X-ClaimCore-Antiforgery", cookie.Token)
        request.Content <- body
        use! response = host.Client.SendAsync(request, cancellation)
        let! bytes = response.Content.ReadAsByteArrayAsync()
        return int response.StatusCode, bytes
    }

let private json text =
    let body = new ByteArrayContent(Encoding.UTF8.GetBytes(text: string))
    body.Headers.ContentType <- MediaTypeHeaderValue "application/json"
    body :> HttpContent

let private awaitCondition message predicate =
    task {
        let watch = Stopwatch.StartNew()

        while not (predicate ()) && watch.Elapsed < TimeSpan.FromSeconds 5. do
            do! Task.Delay 10

        Expect.isTrue (predicate ()) message
    }

let private qualify () =
    task {
        let owner =
            PrincipalKey.human oidc.Issuer.AbsoluteUri "synthetic-human"
            |> Result.defaultWith (string >> failtest)

        let! database = PostgresFixture.createForPool owner 16

        try
            let fixture = database.Fixture
            let! actor = caseWorker fixture

            let other =
                PrincipalKey.human oidc.Issuer.AbsoluteUri "normal-reader"
                |> Result.defaultWith (string >> failtest)

            let! registered =
                Service.execute
                    actor
                    (OperationInput.Authority(authorityRequest (id 10) other AuthorityAct.Register))
                    CancellationToken.None

            acceptedAuthority registered

            let! granted =
                grant
                    actor
                    (id 11)
                    other
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedAuthority granted

            let! opened =
                Service.execute
                    actor
                    (Inputs.caseOperation (
                        request 50 "PRESSURE-CASE" 0L (AuthoredCaseAct.Open registration)
                    ))
                    CancellationToken.None

            accepted opened |> ignore
            let probe = StoreProbe(fixture.Store)

            use host =
                new Host(
                    true,
                    forActor =
                        (fun principal ->
                            Service.create
                                "synthetic"
                                fixture.InstallationId
                                probe
                                fixture.Calendar
                                principal),
                    installationId = fixture.InstallationId
                )

            let! noisy = login host "synthetic-human"
            let! normal = login host "normal-reader"
            let admission = host.Services.GetRequiredService<PrincipalAdmission>()
            use firstBody = new SlowBody()
            use secondBody = new SlowBody()

            let first =
                send host noisy fixture.InstallationId "context" firstBody CancellationToken.None

            let second =
                send host noisy fixture.InstallationId "context" secondBody CancellationToken.None

            let normalSamples = ResizeArray<float>()
            let mutable normalMilliseconds = 0.
            let mutable recoveryMilliseconds = 0.

            try
                do!
                    Task
                        .WhenAll(firstBody.Entered, secondBody.Entered)
                        .WaitAsync(TimeSpan.FromSeconds 5.)

                do!
                    awaitCondition
                        "Both noisy requests actually hold ordinary-read capacity while their bodies are incomplete"
                        (fun () -> admission.ActiveReads = 2L)

                let! rejected, _ =
                    send
                        host
                        noisy
                        fixture.InstallationId
                        "context"
                        (json "{}")
                        CancellationToken.None

                Expect.equal
                    rejected
                    429
                    "Client-chosen request values cannot evade the authenticated principal limit"

                let watch = Stopwatch.StartNew()

                let total = Stopwatch.StartNew()

                for _ in 1..100 do
                    watch.Restart()

                    let! readStatus, readBytes =
                        send
                            host
                            normal
                            fixture.InstallationId
                            "case.list"
                            (json "{\"limit\":1}")
                            CancellationToken.None

                    normalSamples.Add watch.Elapsed.TotalMilliseconds

                    Expect.equal
                        readStatus
                        200
                        "Another actual principal retains ordinary-read capacity"

                    use listing = JsonDocument.Parse readBytes

                    Expect.equal
                        (listing.RootElement
                            .GetProperty("outcome")
                            .GetProperty("data")
                            .GetProperty("items")
                            .GetArrayLength())
                        1
                        "The normal reader observes the independently created PostgreSQL case"

                normalMilliseconds <- total.Elapsed.TotalMilliseconds

                watch.Restart()
                let statusInput = JsonSerializer.Serialize {| operationId = Guid.NewGuid() |}

                let! status, statusBytes =
                    send
                        host
                        noisy
                        fixture.InstallationId
                        "operation.status"
                        (json statusInput)
                        CancellationToken.None

                recoveryMilliseconds <- watch.Elapsed.TotalMilliseconds
                Expect.equal status 200 "Passive status retains its independent workload capacity"
                use passive = JsonDocument.Parse statusBytes

                Expect.equal
                    (passive.RootElement.GetProperty("outcome").GetProperty("kind").GetString())
                    "UNRESOLVED"
                    "Read pressure cannot fabricate permanent nonacceptance"
            finally
                firstBody.Release()
                secondBody.Release()

            let! completed = Task.WhenAll(first, second)

            Expect.isTrue
                (completed |> Array.forall (fst >> (=) 200))
                "Admitted slow-body reads finish after their actual input completes"

            do!
                awaitCondition "Completed HTTP work releases every principal partition" (fun () ->
                    admission.ActivePartitions = 0)

            probe.IgnoreCancellation <- true
            use! locker = database.OwnerSource.OpenConnectionAsync()
            use! observer = database.OwnerSource.OpenConnectionAsync()
            use! transaction = locker.BeginTransactionAsync()

            use block =
                new NpgsqlCommand(
                    "LOCK TABLE claimcore.cases IN ACCESS EXCLUSIVE MODE",
                    locker,
                    transaction
                )

            let! _ = block.ExecuteNonQueryAsync()
            use cancellation = new CancellationTokenSource()

            let heldFirst =
                send
                    host
                    noisy
                    fixture.InstallationId
                    "case.list"
                    (json "{\"limit\":1}")
                    cancellation.Token

            let heldSecond =
                send
                    host
                    noisy
                    fixture.InstallationId
                    "case.list"
                    (json "{\"limit\":1}")
                    cancellation.Token

            let watch = Stopwatch.StartNew()
            let mutable waiting = 0L

            while waiting < 2L && watch.Elapsed < TimeSpan.FromSeconds 5. do
                use command =
                    new NpgsqlCommand(
                        "SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE l.relation='claimcore.cases'::regclass AND NOT l.granted AND a.usename='claimcore_runtime'",
                        observer
                    )

                let! value = command.ExecuteScalarAsync()
                waiting <- unbox<int64> value

                if waiting < 2L then
                    do! Task.Delay 10

            Expect.equal
                waiting
                2L
                "Both actual PostgreSQL readers are independently observed waiting for the owned relation lock"

            cancellation.Cancel()

            do!
                awaitCondition
                    "Cancellation reaches the deliberately uncooperative provider boundary"
                    (fun () -> probe.CancelledReads = 2)

            Expect.equal
                admission.ActiveReads
                2L
                "Client cancellation cannot reuse capacity before actual provider cleanup"

            let! busy, _ =
                send host noisy fixture.InstallationId "context" (json "{}") CancellationToken.None

            Expect.equal busy 429 "A canceled client's unfinished provider work remains protected"
            do! transaction.CommitAsync()

            for pending in [ heldFirst; heldSecond ] do
                try
                    let! _ = pending in ()
                with :? OperationCanceledException ->
                    ()

            do!
                awaitCondition
                    "Provider completion finally releases capacity and partition state"
                    (fun () -> admission.ActiveReads = 0L && admission.ActivePartitions = 0)

            let holders = ResizeArray<NpgsqlConnection>()
            let beforeReads = probe.ObservedReads
            let beforeCancelled = probe.CancelledReads
            use poolCancellation = new CancellationTokenSource()
            let mutable pooled: Task<int * byte array> list = []
            let mutable passivePool: Task<int * byte array> option = None
            let mutable physicalConnections = 0L

            try
                for _ in 1..16 do
                    let! connection = database.RuntimeSource.OpenConnectionAsync()
                    holders.Add connection

                Expect.isTrue
                    (holders
                     |> Seq.forall (fun connection ->
                         connection.State = System.Data.ConnectionState.Open))
                    "Every configured runtime-pool connection is genuinely held"

                use connections =
                    new NpgsqlCommand(
                        "SELECT count(*) FROM pg_stat_activity WHERE usename='claimcore_runtime'",
                        observer
                    )

                let! count = connections.ExecuteScalarAsync()
                physicalConnections <- unbox<int64> count

                Expect.equal
                    physicalConnections
                    16L
                    "Independent PostgreSQL state confirms the actual pool boundary"

                pooled <-
                    [
                        send
                            host
                            noisy
                            fixture.InstallationId
                            "case.list"
                            (json "{\"limit\":1}")
                            poolCancellation.Token
                        send
                            host
                            noisy
                            fixture.InstallationId
                            "case.list"
                            (json "{\"limit\":1}")
                            poolCancellation.Token
                    ]

                do!
                    awaitCondition
                        "Both noisy pool waiters have passed authentication and reached storage"
                        (fun () -> probe.ObservedReads = beforeReads + 2)

                let passive =
                    send
                        host
                        normal
                        fixture.InstallationId
                        "operation.status"
                        (json (JsonSerializer.Serialize {| operationId = Guid.NewGuid() |}))
                        CancellationToken.None

                passivePool <- Some passive

                do!
                    awaitCondition
                        "Both readers and reserved recovery reach the actual exhausted provider pool"
                        (fun () -> probe.ObservedReads = beforeReads + 3)

                Expect.isFalse
                    passive.IsCompleted
                    "Reserved admission does not fabricate an outcome when every physical connection is held"

                poolCancellation.Cancel()

                do!
                    awaitCondition
                        "Pool-wait cancellation reaches both deliberately uncooperative provider calls"
                        (fun () -> probe.CancelledReads = beforeCancelled + 2)

                Expect.equal
                    admission.ActiveReads
                    2L
                    "Pool waiters retain their read permits until actual provider completion"

                let! excess, _ =
                    send
                        host
                        noisy
                        fixture.InstallationId
                        "context"
                        (json "{}")
                        CancellationToken.None

                Expect.equal
                    excess
                    429
                    "Client cancellation cannot evade the principal limit while pool cleanup is unfinished"
            finally
                for connection in holders do
                    connection.Dispose()

            for pending in pooled do
                try
                    let! _ = pending in ()
                with :? OperationCanceledException ->
                    ()

            let! poolStatus, poolBytes = passivePool.Value

            Expect.equal
                poolStatus
                200
                "Recovery completes truthfully after the physical pool is released"

            use recovered = JsonDocument.Parse poolBytes

            Expect.equal
                (recovered.RootElement.GetProperty("outcome").GetProperty("kind").GetString())
                "UNRESOLVED"
                "Pool pressure has created no final promise"

            do!
                awaitCondition
                    "All pool waiters release their partition and workload ownership"
                    (fun () -> admission.ActiveReads = 0L && admission.ActivePartitions = 0)

            let bodies = Array.init 4 (fun _ -> new SlowBody())
            let principalRequests = ResizeArray<Task<int * byte array>>()

            try
                for index in 0..3 do
                    let! cookie = login host ("bounded-holder-" + string index)

                    principalRequests.Add(
                        send
                            host
                            cookie
                            fixture.InstallationId
                            "context"
                            bodies[index]
                            CancellationToken.None
                    )

                    do! bodies[index].Entered.WaitAsync(TimeSpan.FromSeconds 5.)

                    do!
                        awaitCondition
                            "The next distinct slow body has reached actual workload admission"
                            (fun () -> admission.ActiveReads = int64 (index + 1))

                Expect.equal
                    admission.ActiveReads
                    4L
                    "Four distinct authenticated identities reach the process read bound"

                Expect.equal
                    admission.ActivePartitions
                    4
                    "Only the actual slow-body holders remain resident"

                for _ in 1..80 do
                    let! cookie = login host ("bounded-churn-" + Guid.NewGuid().ToString("N"))

                    let! status, _ =
                        send
                            host
                            cookie
                            fixture.InstallationId
                            "context"
                            (json "{}")
                            CancellationToken.None

                    Expect.equal
                        status
                        429
                        "Additional valid protected identities cannot enlarge the process workload budget"

                    Expect.equal
                        admission.ActivePartitions
                        4
                        "Rejected identities leave no retained partition state"

                let! status, _ =
                    send
                        host
                        normal
                        fixture.InstallationId
                        "operation.status"
                        (json (JsonSerializer.Serialize {| operationId = Guid.NewGuid() |}))
                        CancellationToken.None

                Expect.equal
                    status
                    200
                    "Authentication and authorized passive recovery remain usable during principal churn"
            finally
                for body in bodies do
                    body.Release()

            let! principalResults = Task.WhenAll(principalRequests)

            Expect.isTrue
                (principalResults |> Array.forall (fst >> (=) 200))
                "Admitted synthetic identities reach the independent actor-policy boundary after body completion"

            do!
                awaitCondition
                    "Principal churn and actual HTTP cleanup leave no retained identities"
                    (fun () -> admission.ActivePartitions = 0)

            for body in bodies do
                body.Dispose()

            use count =
                new NpgsqlCommand(
                    "SELECT count(*) FROM claimcore.ledger WHERE subject_kind='CONTROL'",
                    observer
                )

            let! controls = count.ExecuteScalarAsync()

            Expect.equal
                (unbox<int64> controls)
                0L
                "Random-ID passive observations have created no retained controls"

            match Environment.GetEnvironmentVariable "CLAIMCORE_TEST_REPORT_DIRECTORY" with
            | null -> ()
            | directory ->
                Directory.CreateDirectory directory |> ignore

                File.WriteAllBytes(
                    Path.Combine(directory, "http-postgres-pressure.json"),
                    JsonSerializer.SerializeToUtf8Bytes
                        {|
                            provider = "postgresql"
                            transport =
                                "TestServer HTTP with protected independent cookie principals"
                            poolCapacity = 16
                            heldSlowBodies = 2
                            additionalProtectedPrincipals = 80
                            processReadBound = 4
                            noisyRejection = 429
                            normalStatus = 200
                            normalReaderSamples = normalSamples.ToArray()
                            normalReaderRequests = normalSamples.Count
                            normalReaderElapsedMilliseconds = normalMilliseconds
                            normalReaderRequestsPerSecond =
                                float normalSamples.Count * 1000. / normalMilliseconds
                            poolPhysicalConnections = physicalConnections
                            passiveStatus = 200
                            passiveMilliseconds = recoveryMilliseconds
                            ignoredCancellationReads = 2
                            retainedControls = 0
                            remainingPartitions = admission.ActivePartitions
                        |}
                )
        finally
            database.Fixture.Dispose()
    }

[<Tests>]
let tests =
    testCaseAsync
        "PostgreSQL HTTP pressure isolates principals and recovery through slow bodies and ignored provider cancellation"
        (async { do! qualify () |> Async.AwaitTask })
