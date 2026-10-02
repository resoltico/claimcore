module ClaimCore.IntegrationTests.BackupControlLifetimeTests

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Database

let private connected action =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    use sender = new TcpClient()
    let accepted = listener.AcceptTcpClientAsync()
    sender.Connect(IPAddress.Loopback, (listener.LocalEndpoint :?> IPEndPoint).Port)
    use receiver = accepted.GetAwaiter().GetResult()
    use input = receiver.GetStream()
    use output = sender.GetStream()
    action input output

let private readCancelled input token =
    Task.Run(fun () ->
        try
            DatabaseBackupControlPipe.readFrameWithCancellation input token |> ignore
            false
        with :? OperationCanceledException ->
            true)

let private trickledFrameHasOneDeadline () =
    connected (fun input output ->
        input.ReadTimeout <- 200
        use stop = new CancellationTokenSource()

        let writer =
            task {
                try
                    while not stop.IsCancellationRequested do
                        do! output.WriteAsync(ReadOnlyMemory<byte>([| byte '{' |]), stop.Token)
                        do! Task.Delay(30, stop.Token)
                with :? OperationCanceledException ->
                    ()
            }

        let read = readCancelled input CancellationToken.None

        try
            Expect.isTrue
                (read.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult())
                "Progress cannot renew the whole-frame deadline"

            Expect.isFalse writer.IsCompleted "Peer remains open and producing bytes"
        finally
            stop.Cancel()
            writer.GetAwaiter().GetResult()
            input.Dispose())

let private captureCancellationReleasesRead () =
    connected (fun input _ ->
        input.ReadTimeout <- 10000
        use stop = new CancellationTokenSource()
        let read = readCancelled input stop.Token
        stop.Cancel()

        Expect.isTrue
            (read.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult())
            "Capture cancellation releases a blocked socket read before its socket timeout")

let tests =
    testList
        "backup control lifetimes"
        [
            testCase
                "[CC-BACKUP-001] trickled control input cannot extend a held frame deadline"
                trickledFrameHasOneDeadline
            testCase
                "[CC-BACKUP-001] capture cancellation releases blocked control input"
                captureCancellationReleasesRead
        ]
