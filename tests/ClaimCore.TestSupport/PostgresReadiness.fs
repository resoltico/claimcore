module ClaimCore.TestSupport.PostgresReadiness

open System
open System.Threading
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Builders
open Npgsql
open Testcontainers.PostgreSql

/// A mapped TCP listener alone does not establish a usable PostgreSQL session.
let hostReady connectionString =
    task {
        use deadline = new CancellationTokenSource(TimeSpan.FromSeconds 1.)
        use connection = new NpgsqlConnection(connectionString)

        try
            do! connection.OpenAsync(deadline.Token)
            use command = new NpgsqlCommand("SELECT 1", connection)
            let! value = command.ExecuteScalarAsync(deadline.Token)
            return Object.Equals(value, box 1)
        with
        | :? NpgsqlException
        | :? IO.IOException
        | :? TimeoutException
        | :? OperationCanceledException -> return false
    }

let strategy () =
    // An explicit strategy suppresses the module default in Testcontainers 4.15.0.
    // Retain its protocol check and mapped TCP check; authenticate over the host path too.
    let protocol =
        [|
            "pg_isready"
            "--host"
            "localhost"
            "--dbname"
            PostgreSqlBuilder.DefaultDatabase
            "--username"
            PostgreSqlBuilder.DefaultUsername
        |]

    let hostDeadline =
        TimeSpan.FromSeconds(float (NpgsqlConnectionStringBuilder().Timeout))

    // Fresh clusters usually initialize in seconds; avoid whole-second polling gaps and
    // the library's one-hour protocol default while retaining both readiness checks.
    let interval = TimeSpan.FromMilliseconds 250.

    Wait
        .ForUnixContainer()
        .UntilCommandIsCompleted(
            protocol,
            fun wait -> wait.WithInterval(interval).WithTimeout(TimeSpan.FromSeconds 60.) |> ignore
        )
        .UntilExternalTcpPortIsAvailable(
            int PostgreSqlBuilder.PostgreSqlPort,
            fun wait -> wait.WithInterval(interval).WithTimeout(hostDeadline) |> ignore
        )
        .AddCustomWaitStrategy(
            { new IWaitUntil with
                member _.UntilAsync container =
                    let settings = NpgsqlConnectionStringBuilder()
                    settings.Host <- container.Hostname

                    settings.Port <-
                        int (container.GetMappedPublicPort(int PostgreSqlBuilder.PostgreSqlPort))

                    settings.Database <- PostgreSqlBuilder.DefaultDatabase
                    settings.Username <- PostgreSqlBuilder.DefaultUsername
                    settings.Password <- PostgreSqlBuilder.DefaultPassword
                    settings.Pooling <- false
                    settings.Timeout <- 1
                    settings.CommandTimeout <- 1
                    hostReady settings.ConnectionString
            },
            fun wait -> wait.WithInterval(interval).WithTimeout(hostDeadline) |> ignore
        )
