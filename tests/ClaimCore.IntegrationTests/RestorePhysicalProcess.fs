module internal ClaimCore.IntegrationTests.RestorePhysicalProcess

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Expecto

let privateScratch () =
    let temporary = Path.GetTempPath()

    let physical =
        if
            OperatingSystem.IsMacOS()
            && (temporary.StartsWith("/var/", StringComparison.Ordinal)
                || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
        then
            "/private" + temporary
        else
            temporary

    let path =
        Path.Combine(physical, "claimcore-physical-restore-" + Guid.NewGuid().ToString("N"))

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(path, mode) |> ignore
    path

let private retainStderr bytes =
    try
        let directory = privateScratch ()

        ClaimCore.HostSecurity.PrivateFileService.writeNew
            1_048_576
            (Path.Combine(directory, "stderr.txt"))
            bytes
        |> ignore
    with _ ->
        ()

let run command arguments =
    let info = ProcessStartInfo(command)
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true

    for value in arguments do
        info.ArgumentList.Add(value)

    let result = ClaimCore.TestSupport.BoundedProcess.run info None 1_048_576 240_000

    if result.ExitCode <> 0 then
        retainStderr result.StandardError

    let utf8 = System.Text.UTF8Encoding(false, true)
    let diagnosticText = utf8.GetString(result.StandardError)

    let safe =
        Regex.Match(
            diagnosticText,
            "(?:restore-stage|copy-proof-stage|wal-capture-stage)=[a-z-]{1,70}"
        )

    let reason = Regex.Match(diagnosticText, "\"reason\"\\s*:\\s*\"([A-Z_]{1,70})\"")

    let backupReason =
        Regex.Match(diagnosticText, "\"reason\"\\s*:\\s*\"([a-z0-9-]{1,70})\"")

    let stage =
        if safe.Success then
            safe.Value
        elif reason.Success then
            "restore-stage=" + reason.Groups[1].Value.ToLowerInvariant().Replace('_', '-')
        elif backupReason.Success then
            "restore-stage=" + backupReason.Groups[1].Value
        else
            "restore-stage=unavailable"

    result.ExitCode, utf8.GetString(result.StandardOutput).Trim(), stage
