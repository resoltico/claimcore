module ClaimCore.IntegrationTests.CommitProxy

open System
open System.Buffers.Binary
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks

[<RequireQualifiedAccess>]
type internal Drop =
    | BeforeCommit
    | AfterCommit
    | AfterCommitFatal
    | Never

let private packet (stream: NetworkStream) token =
    task {
        let header = Array.zeroCreate<byte> 5
        do! stream.ReadExactlyAsync(Memory header, token).AsTask()
        let length = BinaryPrimitives.ReadInt32BigEndian(ReadOnlySpan(header, 1, 4))

        if length < 4 || length > 1048576 then
            invalidOp "Invalid synthetic proxy packet."

        let body = Array.zeroCreate<byte>(length - 4)
        do! stream.ReadExactlyAsync(Memory body, token).AsTask()
        return Array.append header body
    }

let private startup (client: NetworkStream) (server: NetworkStream) token =
    task {
        let header = Array.zeroCreate<byte> 4
        do! client.ReadExactlyAsync(Memory header, token).AsTask()
        let length = BinaryPrimitives.ReadInt32BigEndian(ReadOnlySpan header)

        if length < 8 || length > 65536 then
            invalidOp "Invalid synthetic proxy startup."

        let body = Array.zeroCreate<byte>(length - 4)
        do! client.ReadExactlyAsync(Memory body, token).AsTask()
        do! server.WriteAsync(ReadOnlyMemory(Array.append header body), token).AsTask()
    }

let private query (bytes: byte array) =
    if bytes[0] = byte 'Q' || bytes[0] = byte 'P' then
        Encoding.UTF8.GetString(bytes, 5, bytes.Length - 5).Split('\000')
        |> Array.exists (fun part ->
            part.Trim().TrimEnd(';').Equals("COMMIT", StringComparison.OrdinalIgnoreCase))
    else
        false

let private committed (bytes: byte array) =
    bytes[0] = byte 'C'
    && Encoding.UTF8
        .GetString(bytes, 5, bytes.Length - 5)
        .StartsWith("COMMIT", StringComparison.Ordinal)

let private errorState (bytes: byte array) =
    Encoding.UTF8.GetString(bytes, 5, bytes.Length - 5).Split('\000')
    |> Array.tryFind _.StartsWith("C", StringComparison.Ordinal)
    |> Option.map (fun field -> field.Substring 1)

/// A protocol relay drops a real connection at the commit boundary; no store result is mocked.
type internal Proxy(host: string, port: int, drop: Drop, ?terminate: int -> Task<unit>) =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let cancellation = new CancellationTokenSource()

    let intercepted =
        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable fatalSqlState: string option = None

    let relayBackend (clientStream: NetworkStream) (serverStream: NetworkStream) close =
        task {
            let mutable backendId = 0
            let mutable awaitFatal = false

            while not cancellation.IsCancellationRequested do
                let! bytes = packet serverStream cancellation.Token

                if bytes[0] = byte 'K' then
                    backendId <- BinaryPrimitives.ReadInt32BigEndian(ReadOnlySpan(bytes, 5, 4))

                let committed = committed bytes

                if committed && drop = Drop.AfterCommitFatal then
                    awaitFatal <- true

                    do!
                        (terminate
                         |> Option.defaultWith (fun () -> invalidOp "Backend controller required."))
                            backendId
                elif awaitFatal && bytes[0] = byte 'E' then
                    fatalSqlState <- errorState bytes

                    do! clientStream.WriteAsync(ReadOnlyMemory bytes, cancellation.Token).AsTask()

                    intercepted.TrySetResult() |> ignore
                    close ()
                elif awaitFatal then
                    ()
                elif committed && drop = Drop.AfterCommit then
                    intercepted.TrySetResult() |> ignore
                    close ()
                else
                    do! clientStream.WriteAsync(ReadOnlyMemory bytes, cancellation.Token).AsTask()
        }

    let serve () =
        task {
            use! client = listener.AcceptTcpClientAsync(cancellation.Token)
            use server = new TcpClient()
            do! server.ConnectAsync(host, port, cancellation.Token)
            use clientStream = client.GetStream()
            use serverStream = server.GetStream()
            do! startup clientStream serverStream cancellation.Token

            let close () =
                client.Close()
                server.Close()

            let frontend =
                task {
                    while not cancellation.IsCancellationRequested do
                        let! bytes = packet clientStream cancellation.Token

                        if query bytes && drop = Drop.BeforeCommit then
                            intercepted.TrySetResult() |> ignore
                            close ()
                        else
                            do!
                                serverStream
                                    .WriteAsync(ReadOnlyMemory bytes, cancellation.Token)
                                    .AsTask()
                }

            let backend = relayBackend clientStream serverStream close
            let! _ = Task.WhenAny(frontend, backend)
            close ()

            try
                let! _ = Task.WhenAll(frontend, backend) in ()
            with
            | :? IOException
            | :? SocketException
            | :? OperationCanceledException
            | :? ObjectDisposedException -> ()
        }

    do listener.Start()
    let serving = serve ()
    member _.Port = (listener.LocalEndpoint :?> IPEndPoint).Port
    member _.Intercepted = intercepted.Task
    member _.FatalSqlState = fatalSqlState

    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            listener.Stop()

            try
                serving.GetAwaiter().GetResult()
            with
            | :? OperationCanceledException
            | :? SocketException -> ()

            cancellation.Dispose()
