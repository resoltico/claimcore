namespace ClaimCore.Database

open System
open System.IO
open ClaimCore.HostSecurity
open ClaimCore.Postgres

module DatabaseExecution =
    let private ownerConnection () =
        match
            Environment.GetEnvironmentVariable("CLAIMCORE_ADMIN_CONNECTION_FILE")
            |> Option.ofObj
        with
        | None -> Error DatabaseInputProblem.ConnectionSettingMissing
        | Some path ->
            match PrivateFileService.readUtf8Text 8192 path with
            | Error _ -> Error DatabaseInputProblem.ConnectionFileRefused
            | Ok value when String.IsNullOrWhiteSpace value ->
                Error DatabaseInputProblem.ConnectionFileEmpty
            | Ok value -> Ok(value.Trim())

    let private execute connection =
        function
        | DatabaseCommand.Migrate ->
            Migrations.apply connection |> AdministrationOutcome.map (fun () -> None)
        | DatabaseCommand.SetBusinessZone zone ->
            InstallationBusinessZone.set connection zone
            |> AdministrationOutcome.map (fun () -> None)
        | DatabaseCommand.Prune options ->
            PreparationPruning.prune connection options |> AdministrationOutcome.map Some
        | _ -> AdministrationOutcome.NotStarted AdministrationFailure.OperationFailed

    let private write (stream: Stream) (bytes: byte array) =
        stream.Write(bytes, 0, bytes.Length)
        stream.Flush()

    let private bestEffort stream encode =
        try
            write stream (encode ())
        with _ ->
            ()

    /// The operation has already returned. Neither encoding, writing nor flushing can relabel it.
    let deliver command result (output: Stream) (errors: Stream) =
        let code = DatabaseDiagnostics.exitCode result
        let target = if code = 0 then output else errors

        try
            write target (DatabaseDiagnostics.outcome command result)
            code
        with _ ->
            if code = 0 then
                bestEffort errors (fun () -> DatabaseDiagnostics.deliveryFailure command result)

            if code = 4 then 4 else 3

    let inputFailure reason errors code =
        bestEffort errors (fun () -> DatabaseDiagnostics.inputFailure reason)
        code

    let run command output errors =
        match ownerConnection () with
        | Error reason -> inputFailure reason errors 3
        | Ok connection ->
            let result = execute connection command
            deliver command result output errors
