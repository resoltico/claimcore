module internal ClaimCore.AcceptanceTests.CommitReplyProxy

open System
open System.Buffers.Binary
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

let private maximumInspectedLength = 1_048_576

let private forwardPacket (source: NetworkStream) (target: NetworkStream) inspect token =
    task {
        let header = Array.zeroCreate<byte> 5
        do! source.ReadExactlyAsync(Memory header, token).AsTask()
        let length = BinaryPrimitives.ReadInt32BigEndian(ReadOnlySpan(header, 1, 4))
        let carriesAttachment = header[0] = byte 'B' || header[0] = byte 'D'

        if
            length < 4
            || length > AttachmentLimits.FileBytes + maximumInspectedLength
            || (length > maximumInspectedLength && not carriesAttachment)
        then
            invalidOp "Invalid synthetic PostgreSQL packet."

        if length > maximumInspectedLength then
            // Bind parameters and DataRow fields can carry an attachment. Neither changes
            // commit observation, so relay it with bounded scratch space.
            do! target.WriteAsync(ReadOnlyMemory header, token).AsTask()
            let buffer = Array.zeroCreate<byte> 65_536
            let mutable remaining = length - 4

            while remaining > 0 do
                let count = min remaining buffer.Length
                do! source.ReadExactlyAsync(Memory(buffer, 0, count), token).AsTask()
                do! target.WriteAsync(ReadOnlyMemory(buffer, 0, count), token).AsTask()
                remaining <- remaining - count

            return true
        else
            let bytes = Array.zeroCreate<byte>(length + 1)
            header.CopyTo(bytes, 0)
            do! source.ReadExactlyAsync(Memory(bytes, 5, length - 4), token).AsTask()
            let forward = inspect bytes

            if forward then
                do! target.WriteAsync(ReadOnlyMemory bytes, token).AsTask()

            return forward
    }

let private startup (client: NetworkStream) (server: NetworkStream) token =
    task {
        let header = Array.zeroCreate<byte> 4
        do! client.ReadExactlyAsync(Memory header, token).AsTask()
        let length = BinaryPrimitives.ReadInt32BigEndian(ReadOnlySpan header)

        if length < 8 || length > 65_536 then
            invalidOp "Invalid synthetic PostgreSQL startup."

        let body = Array.zeroCreate<byte>(length - 4)
        do! client.ReadExactlyAsync(Memory body, token).AsTask()
        do! server.WriteAsync(ReadOnlyMemory(Array.append header body), token).AsTask()
    }

let private carries (text: string) (bytes: byte array) =
    Encoding.UTF8
        .GetString(bytes, 5, bytes.Length - 5)
        .Contains(text, StringComparison.OrdinalIgnoreCase)

/// Count head-lock commits globally, so staging and acceptance may use different connections.
type private Control() =
    let gate = obj ()
    let mutable armed: (int * TaskCompletionSource) option = None

    member _.Arm(commitNumber) =
        if commitNumber < 1 then
            invalidArg (nameof commitNumber) "Select a positive head-write commit."

        lock gate (fun () ->
            if armed.IsSome then
                invalidOp "Synthetic commit loss is already armed."

            let completion =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            armed <- Some(commitNumber, completion)
            completion.Task)

    member _.Take() =
        lock gate (fun () ->
            match armed with
            | None -> None
            | Some(remaining, completion) when remaining > 1 ->
                armed <- Some(remaining - 1, completion)
                None
            | Some(_, completion) ->
                armed <- None
                Some completion)

type private Transaction() =
    let gate = obj ()
    let mutable writing = false

    member _.Observe(bytes: byte array) =
        if
            (bytes[0] = byte 'Q' || bytes[0] = byte 'P')
            && carries "claimcore.lock_ledger_head" bytes
        then
            lock gate (fun () -> writing <- true)

    member _.Commit(bytes: byte array) =
        if bytes[0] = byte 'Z' && bytes.Length = 6 && bytes[5] = byte 'I' then
            // Idle ReadyForQuery clears rolled-back writes before this pooled connection is reused.
            lock gate (fun () -> writing <- false)
            false
        elif bytes[0] = byte 'C' && carries "COMMIT" bytes then
            lock gate (fun () ->
                let value = writing in
                writing <- false
                value)
        else
            false

let private frontend
    (client: NetworkStream)
    (server: NetworkStream)
    (transaction: Transaction)
    (token: CancellationToken)
    =
    task {
        let inspect bytes =
            transaction.Observe bytes
            true

        while not token.IsCancellationRequested do
            let! _ = forwardPacket client server inspect token
            ()
    }

[<NoEquality; NoComparison>]
type private BackendRelay =
    {
        Client: NetworkStream
        Server: NetworkStream
        Transaction: Transaction
        Control: Control
        Close: unit -> unit
        CancellationToken: CancellationToken
    }

let private backend (relay: BackendRelay) =
    let client = relay.Client
    let server = relay.Server
    let transaction = relay.Transaction
    let control = relay.Control
    let close = relay.Close
    let token = relay.CancellationToken

    let inspect bytes =
        let interception = if transaction.Commit bytes then control.Take() else None

        match interception with
        | Some completion ->
            // The actual server emitted COMMIT completion. Withhold it from Npgsql.
            close ()
            completion.TrySetResult() |> ignore
            false
        | None -> true

    task {
        let mutable active = true

        while active && not token.IsCancellationRequested do
            let! forwarded = forwardPacket server client inspect token
            active <- forwarded
    }

let private serve
    (host: string)
    (port: int)
    control
    (client: TcpClient)
    (token: CancellationToken)
    =
    task {
        use client = client
        use server = new TcpClient()
        client.NoDelay <- true
        server.NoDelay <- true

        try
            do! server.ConnectAsync(host, port, token)
            use incoming = client.GetStream()
            use outgoing = server.GetStream()
            do! startup incoming outgoing token
            let transaction = Transaction()

            let close () =
                client.Close()
                server.Close()

            let upstream = frontend incoming outgoing transaction token

            let downstream =
                backend
                    {
                        Client = incoming
                        Server = outgoing
                        Transaction = transaction
                        Control = control
                        Close = close
                        CancellationToken = token
                    }

            let! _ = Task.WhenAny(upstream, downstream)
            close ()
            let! _ = Task.WhenAll(upstream, downstream)
            return ()
        with
        | :? IOException
        | :? SocketException
        | :? OperationCanceledException
        | :? ObjectDisposedException -> ()
    }

/// The fixture supplies only its own fresh synthetic database connection.
type Proxy(host: string, port: int) =
    let listener = new TcpListener(IPAddress.Loopback, 0)
    let cancellation = new CancellationTokenSource()
    let control = Control()
    let sessions = ConcurrentBag<Task>()
    do listener.Start()

    let accepting =
        task {
            try
                while not cancellation.IsCancellationRequested do
                    let! client = listener.AcceptTcpClientAsync(cancellation.Token)
                    sessions.Add(serve host port control client cancellation.Token)
            with
            | :? SocketException
            | :? OperationCanceledException
            | :? ObjectDisposedException -> ()
        }

    member _.Port = (listener.LocalEndpoint :?> IPEndPoint).Port
    member _.Arm(commitNumber) = control.Arm commitNumber

    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            listener.Stop()
            accepting.GetAwaiter().GetResult()
            Task.WhenAll(sessions.ToArray()).GetAwaiter().GetResult()
            cancellation.Dispose()
