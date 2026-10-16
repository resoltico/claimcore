module ClaimCore.TestSupport.PostgresReadiness

open System
open DotNet.Testcontainers.Builders
open Npgsql
open Testcontainers.PostgreSql

let strategy () =
    // An explicit strategy suppresses the module default in Testcontainers 4.15.0.
    // Keep its exact protocol check, then verify the mapped port from the test host.
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
