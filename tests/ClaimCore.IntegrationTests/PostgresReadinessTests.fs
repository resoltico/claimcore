module ClaimCore.IntegrationTests.PostgresReadinessTests

open System
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

[<Tests>]
let tests =
    testList
        "PostgreSQL readiness"
        [
            testCase "protocol and mapped-port checks retain explicit bounded polling" (fun () ->
                let _, configured = waits ()
                Expect.equal configured.Length 3 "Running container plus two readiness checks"

                for wait in configured[1..] do
                    Expect.equal wait.Interval (TimeSpan.FromMilliseconds 250.) "Poll promptly"

                Expect.equal configured[1].Timeout (TimeSpan.FromSeconds 60.) "Protocol deadline"

                Expect.equal
                    configured[2].Timeout
                    (TimeSpan.FromSeconds(float (Npgsql.NpgsqlConnectionStringBuilder().Timeout)))
                    "Mapped-port deadline retains the connection timeout")
            testCaseAsync
                "running container without PostgreSQL times out"
                (async { do! unavailableProtocol () |> Async.AwaitTask })
        ]
