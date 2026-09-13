module ClaimCore.AcceptanceTests.ProcessRunner

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text

[<NoEquality; NoComparison>]
type Result =
    {
        ExitCode: int
        StandardOutput: byte array
        StandardError: byte array
    }

let private maximumOutputBytes = 1_048_576

let private bytes name (value: string) =
    let encoded = Encoding.UTF8.GetBytes(value)

    if encoded.Length > maximumOutputBytes then
        invalidOp ($"Published process exceeded the bounded {name} allowance.")

    encoded

let dotnet
    (timeout: int)
    (dll: string)
    (arguments: string list)
    (environment: IReadOnlyDictionary<string, string>)
    (standardInput: byte array option)
    =
    let start = ProcessStartInfo("dotnet")
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.RedirectStandardInput <- standardInput.IsSome
    start.StandardOutputEncoding <- UTF8Encoding(false, true)
    start.StandardErrorEncoding <- UTF8Encoding(false, true)
    start.ArgumentList.Add(dll)

    start.Environment.Keys
    |> Seq.filter (fun name ->
        name.StartsWith("CLAIMCORE_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("COVERLET_", StringComparison.OrdinalIgnoreCase))
    |> Seq.toArray
    |> Array.iter (fun name -> start.Environment.Remove(name) |> ignore)

    for argument in arguments do
        start.ArgumentList.Add(argument)

    for pair in environment do
        start.Environment[pair.Key] <- pair.Value

    start.Environment["DOTNET_NOLOGO"] <- "1"
    start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
    start.Environment["NO_COLOR"] <- "1"
    start.Environment["TERM"] <- "dumb"

    use child = new Process(StartInfo = start)

    if not (child.Start()) then
        invalidOp "Published process did not start."

    standardInput
    |> Option.iter (fun input ->
        child.StandardInput.BaseStream.Write(input, 0, input.Length)
        child.StandardInput.Close())

    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit(timeout)) then
        try
            child.Kill(true)
        with _ ->
            ()

        invalidOp "Published process exceeded its time limit."

    {
        ExitCode = child.ExitCode
        StandardOutput = stdout.GetAwaiter().GetResult() |> bytes "standard-output"
        StandardError = stderr.GetAwaiter().GetResult() |> bytes "standard-error"
    }
