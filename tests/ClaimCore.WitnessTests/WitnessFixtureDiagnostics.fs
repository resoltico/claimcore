module ClaimCore.WitnessTests.WitnessFixtureDiagnostics

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Npgsql
open Testcontainers.PostgreSql
open ClaimCore.TestSupport
open ClaimCore.HostSecurity

[<NoEquality; NoComparison>]
type Facts =
    {
        Format: string
        ContainerId: string
        Phase: string
        SqlState: string
        CredentialAgreement: Nullable<bool>
        HostAgreement: Nullable<bool>
        PortAgreement: Nullable<bool>
        Running: Nullable<bool>
    }

let private initial (container: PostgreSqlContainer) phase (error: exn) =
    {
        Format = "claimcore-witness-fixture-failure-1"
        ContainerId = container.Id
        Phase = phase
        SqlState =
            match error with
            | :? PostgresException as failure when Regex.IsMatch(failure.SqlState, "^[A-Z0-9]{5}$") ->
                failure.SqlState
            | _ -> "unknown"
        CredentialAgreement = Nullable()
        HostAgreement = Nullable()
        PortAgreement = Nullable()
        Running = Nullable()
    }

let private inspectOwned (container: PostgreSqlContainer) =
    let info =
        ProcessStartInfo(
            "docker",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for value in
        [
            "inspect"
            "--format"
            "{\"env\":{{json .Config.Env}},\"running\":{{json .State.Running}},\"ports\":{{json .NetworkSettings.Ports}}}"
            container.Id
        ] do
        info.ArgumentList.Add(value)

    BoundedProcess.run info None 65536 5000


let inspect (container: PostgreSqlContainer) selected supplied phase error =
    let facts = initial container phase error
    let result = inspectOwned container

    if result.ExitCode <> 0 then
        facts
    else
        use document = JsonDocument.Parse(result.StandardOutput)
        let root = document.RootElement
        let environment = root.GetProperty("env").EnumerateArray()

        let actual =
            environment
            |> Seq.choose (fun value ->
                match value.GetString() |> Option.ofObj with
                | Some value when value.StartsWith("POSTGRES_PASSWORD=", StringComparison.Ordinal) ->
                    Some(value.Substring(18))
                | _ -> None)
            |> Seq.tryExactlyOne

        match selected with
        | None ->
            { facts with
                Running = Nullable(root.GetProperty("running").GetBoolean())
            }
        | Some(connection: string) ->
            let client = NpgsqlConnectionStringBuilder(connection)
            let ports = root.GetProperty("ports").GetProperty("5432/tcp")

            { facts with
                CredentialAgreement =
                    match actual with
                    | Some value -> Nullable(value = supplied && client.Password = supplied)
                    | None -> Nullable()
                HostAgreement = Nullable(client.Host = container.Hostname)
                PortAgreement =
                    if ports.ValueKind = JsonValueKind.Array then
                        Nullable(
                            ports.EnumerateArray()
                            |> Seq.exists (fun value ->
                                value.GetProperty("HostPort").GetString() = string client.Port)
                        )
                    else
                        Nullable()
                Running = Nullable(root.GetProperty("running").GetBoolean())
            }

let write directory facts =
    PrivateFileService.writeNew
        16384
        (Path.Combine(directory, "witness-startup.json"))
        (JsonSerializer.SerializeToUtf8Bytes(facts))

let retain capture (original: exn) =
    try
        capture ()
    with _ ->
        ()

    original

let capture container selected supplied phase error =
    retain
        (fun () ->
            let temporary = DirectoryInfo(Path.GetTempPath()).FullName

            let physical =
                if
                    OperatingSystem.IsMacOS()
                    && (temporary.StartsWith("/var/", StringComparison.Ordinal)
                        || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
                then
                    "/private" + temporary
                else
                    temporary

            let directory =
                Path.Combine(physical, "claimcore-witness-startup-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
            )
            |> ignore

            let facts =
                try
                    inspect container selected supplied phase error
                with _ ->
                    initial container phase error

            write directory facts |> ignore)
        error
    |> ignore
