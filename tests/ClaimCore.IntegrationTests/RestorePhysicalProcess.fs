module internal ClaimCore.IntegrationTests.RestorePhysicalProcess

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Expecto

let run command arguments =
    let info = ProcessStartInfo(command)
    info.UseShellExecute <- false
    info.RedirectStandardOutput <- true
    info.RedirectStandardError <- true

    for value in arguments do
        info.ArgumentList.Add(value)

    use child = new Process(StartInfo = info)

    if not (child.Start()) then
        failtest "Isolated restore process could not start"

    let output = child.StandardOutput.ReadToEndAsync()
    let diagnostics = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit(240000)) then
        child.Kill(true)
        failtest "Isolated restore process timed out"

    let diagnosticText = diagnostics.GetAwaiter().GetResult()

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

    child.ExitCode, output.GetAwaiter().GetResult().Trim(), stage

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
