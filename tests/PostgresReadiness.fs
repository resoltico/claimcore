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

    Wait
        .ForUnixContainer()
        .UntilCommandIsCompleted(protocol)
        .UntilExternalTcpPortIsAvailable(
            int PostgreSqlBuilder.PostgreSqlPort,
            fun wait -> wait.WithTimeout(hostDeadline) |> ignore
        )
