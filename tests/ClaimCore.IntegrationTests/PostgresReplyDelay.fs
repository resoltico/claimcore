module internal ClaimCore.IntegrationTests.PostgresReplyDelay

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.IntegrationTests.PostgresProtocolFrames

/// Withhold one identified reply after explicit arming, never connection bootstrap.
/// Cancellation sessions remain transparent; no SQL or wire bytes leave this fixture.
type Relay(connectionString: string, statement: string, maximumHold: TimeSpan) =
    let destination = NpgsqlConnectionStringBuilder(connectionString)
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let stop = new CancellationTokenSource()
    let sessions = ConcurrentBag<Task>()
    let clients = ConcurrentBag<TcpClient>()
    let mutable armed = false

    let held =
        TaskCompletionSource<char>(TaskCreationOptions.RunContinuationsAsynchronously)

    let released =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let forwardEnd (client: TcpClient) =
        try
            client.Client.Shutdown(SocketShutdown.Send)
        with
        | :? SocketException -> ()
        | :? ObjectDisposedException -> ()

    let copy (source: TcpClient) (target: TcpClient) handle =
        task {
            let buffer = Array.zeroCreate<byte> 8192
            let mutable reading = true

            while reading do
                let! count = source.GetStream().ReadAsync(buffer.AsMemory(), stop.Token)
                reading <- count > 0

                if reading then
                    do! handle (target.GetStream()) buffer count

            forwardEnd target
        }

    let transparent (target: NetworkStream) (bytes: byte array) count =
        task { do! target.WriteAsync(bytes.AsMemory(0, count), stop.Token) }

    let selectedPumps (client: TcpClient) (server: TcpClient) =
        let frontend = Parser()
        let backend = Parser()
        let selection = ReplySelection(statement)

        let outbound target bytes count =
            task {
                if Volatile.Read(&armed) then
                    frontend.Feed(bytes, count) |> Array.iter selection.Observe

                do! transparent target bytes count
            }

        let inbound (target: NetworkStream) bytes count =
            task {
                if not (Volatile.Read(&armed)) then
                    do! transparent target bytes count
                else
                    for frame in backend.Feed(bytes, count) do
                        let kind = char frame[0]

                        if selection.Matches(frame) && held.TrySetResult(kind) then
                            do! released.Task.WaitAsync(maximumHold, stop.Token)

                        do! target.WriteAsync(frame.AsMemory(), stop.Token)
            }

        copy client server outbound, copy server client inbound

    let serve selected (client: TcpClient) =
        task {
            use client = client
            use server = new TcpClient()
            clients.Add(server)
            do! server.ConnectAsync(nonNull destination.Host, destination.Port, stop.Token)

            let outbound, inbound =
                if selected then
                    selectedPumps client server
                else
                    copy client server transparent, copy server client transparent

            try
                do! Task.WhenAll(outbound :> Task, inbound :> Task)
            with
            | :? OperationCanceledException when stop.IsCancellationRequested -> ()
            | :? IOException when stop.IsCancellationRequested -> ()
            | :? SocketException when stop.IsCancellationRequested -> ()
            | :? ObjectDisposedException when stop.IsCancellationRequested -> ()
        }

    do listener.Start()

    let accepting =
        task {
            let mutable first = true

            try
                while not stop.IsCancellationRequested do
                    let! client = listener.AcceptTcpClientAsync(stop.Token)
                    clients.Add(client)
                    let session = serve first client :> Task
                    first <- false
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

    member _.Arm() = Volatile.Write(&armed, true)
    member _.Held = held.Task
    member _.Release() = released.TrySetResult() |> ignore

    interface IDisposable with
        member _.Dispose() =
            stop.Cancel()
            listener.Stop()
            accepting.WaitAsync(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult()

            for client in clients do
                client.Dispose()

            let all = Task.WhenAll(sessions.ToArray())

            try
                all.WaitAsync(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult()
            finally
                stop.Dispose()
