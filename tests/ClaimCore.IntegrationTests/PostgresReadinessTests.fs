module ClaimCore.IntegrationTests.PostgresReadinessTests

open System
open System.Net
open System.Net.Sockets
open Expecto
open DotNet.Testcontainers.Builders
open ClaimCore.TestSupport

let private waits () =
    let strategy = PostgresReadiness.strategy ()
    strategy, strategy.Build() |> Seq.toArray

let private unavailableProtocol () =
    task {
        let strategy, configured = waits ()
        configured[1].WithTimeout(TimeSpan.FromMilliseconds 500.) |> ignore

        let container =
            ContainerBuilder(PostgresBaseline.image ())
                .WithLabel("claimcore.test", "readiness-refusal")
                .WithLabel("claimcore.test-run", Guid.NewGuid().ToString("D"))
                .WithEntrypoint("sleep")
                .WithCommand("30")
                .WithWaitStrategy(strategy)
                .Build()

        try
            try
                do! container.StartAsync()
                failtest "A running container without PostgreSQL cannot be ready"
            with :? TimeoutException ->
                ()
        finally
            container.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

let private hostProtocol () =
    async {
        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port

        let accept () =
            task {
                use! client = listener.AcceptTcpClientAsync()
                client.Close()
            }

        let first = accept ()
        use client = new TcpClient()
        do! client.ConnectAsync(IPAddress.Loopback, port) |> Async.AwaitTask
        do! first |> Async.AwaitTask
        let second = accept ()
        let settings = Npgsql.NpgsqlConnectionStringBuilder()
        settings.Host <- "127.0.0.1"
        settings.Port <- port
        settings.Username <- "synthetic"
        settings.Password <- "synthetic"
        settings.Pooling <- false
        settings.Timeout <- 1
        let! ready = PostgresReadiness.hostReady settings.ConnectionString |> Async.AwaitTask
        do! second |> Async.AwaitTask
        Expect.isFalse ready "TCP acceptance cannot stand in for a host PostgreSQL session"
    }

[<Tests>]
let tests =
    testList
        "PostgreSQL readiness"
        [
            testCase
                "protocol mapped-port and authenticated host checks retain bounded polling"
                (fun () ->
                    let _, configured = waits ()

                    Expect.equal
                        configured.Length
                        4
                        "Running container plus three readiness checks"

                    for wait in configured[1..] do
                        Expect.equal wait.Interval (TimeSpan.FromMilliseconds 250.) "Poll promptly"

                    Expect.equal
                        configured[1].Timeout
                        (TimeSpan.FromSeconds 60.)
                        "Protocol deadline"

                    Expect.equal
                        configured[2].Timeout
                        (TimeSpan.FromSeconds(
                            float (Npgsql.NpgsqlConnectionStringBuilder().Timeout)
                        ))
                        "Mapped-port deadline retains the connection timeout"

                    Expect.equal
                        configured[3].Timeout
                        configured[2].Timeout
                        "Host authentication keeps the same deadline")
            testCaseAsync "a TCP-only listener cannot report PostgreSQL ready" (hostProtocol ())
            testCaseAsync
                "running container without PostgreSQL times out"
                (async { do! unavailableProtocol () |> Async.AwaitTask })
        ]
