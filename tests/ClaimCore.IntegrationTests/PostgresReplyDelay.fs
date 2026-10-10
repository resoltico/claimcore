module internal ClaimCore.IntegrationTests.PostgresReplyDelay

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Npgsql

/// Synthetic TCP relay delays replies only after the selected SQL text has been dispatched.
/// Buffers and diagnostics never leave this fixture; cancellation packets use separate sessions.
type Relay(connectionString: string, statement: string, delay: TimeSpan) =
    let destination = NpgsqlConnectionStringBuilder(connectionString)
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let stop = new CancellationTokenSource()
    let sessions = ConcurrentBag<Task>()
    let clients = ConcurrentBag<TcpClient>()

    let dispatched =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let pump (source: NetworkStream) (target: NetworkStream) observe shouldDelay forwarded =
        task {
            let buffer = Array.zeroCreate<byte> 8192
            let mutable reading = true

            while reading do
                let! count = source.ReadAsync(buffer.AsMemory(), stop.Token)
                reading <- count <> 0

                if reading then
                    observe buffer count

                    if shouldDelay () then
                        do! Task.Delay(delay, stop.Token)

                    do! target.WriteAsync(buffer.AsMemory(0, count), stop.Token)
                    forwarded ()
        }

    let serve (client: TcpClient) =
        task {
            use client = client
            use server = new TcpClient()
            clients.Add(server)
            do! server.ConnectAsync(nonNull destination.Host, destination.Port, stop.Token)
            let mutable trail = ""
            let mutable pendingDelay = 0
            let mutable matchedRequest = false

            let observe bytes count =
                let text = trail + Encoding.ASCII.GetString(bytes, 0, count)

                if text.Contains(statement, StringComparison.Ordinal) then
                    Interlocked.Exchange(&pendingDelay, 1) |> ignore
                    matchedRequest <- true

                trail <- text.Substring(max 0 (text.Length - statement.Length))

            let forwarded () =
                if matchedRequest then
                    matchedRequest <- false
                    dispatched.TrySetResult() |> ignore

            let outbound =
                pump (client.GetStream()) (server.GetStream()) observe (fun () -> false) forwarded

            let inbound =
                pump
                    (server.GetStream())
                    (client.GetStream())
                    (fun _ _ -> ())
                    (fun () -> Interlocked.Exchange(&pendingDelay, 0) <> 0)
                    (fun () -> ())

            try
                do! Task.WhenAll(outbound :> Task, inbound :> Task)
            with
            | :? OperationCanceledException when stop.IsCancellationRequested -> ()
            | :? IO.IOException when stop.IsCancellationRequested -> ()
            | :? SocketException when stop.IsCancellationRequested -> ()
            | :? ObjectDisposedException when stop.IsCancellationRequested -> ()
        }

    do listener.Start()

    let accepting =
        task {
            try
                while not stop.IsCancellationRequested do
                    let! client = listener.AcceptTcpClientAsync(stop.Token)
                    clients.Add(client)
                    let session = serve client :> Task
                    sessions.Add(session)
            with _ when stop.IsCancellationRequested ->
                ()
        }

    member _.ConnectionString =
        let proxy = NpgsqlConnectionStringBuilder(connectionString)
        proxy.Host <- "127.0.0.1"
        proxy.Port <- (listener.LocalEndpoint :?> IPEndPoint).Port
        proxy.SslMode <- SslMode.Disable
        proxy.ConnectionString

    member _.Dispatched = dispatched.Task

    interface IDisposable with
        member _.Dispose() =
            stop.Cancel()
            listener.Stop()

            accepting.WaitAsync(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult()

            for client in clients do
                client.Dispose()

            let all = Task.WhenAll(Array.append [| accepting :> Task |] (sessions.ToArray()))

            try
                all.WaitAsync(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult()
            finally
                stop.Dispose()
