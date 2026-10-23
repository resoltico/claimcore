module ClaimCore.TestSupport.OutputControls

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO

let private runProcess directory executable arguments environment input =
    let start =
        ProcessStartInfo(
            executable,
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for argument in arguments do
        start.ArgumentList.Add argument

    CliProcessEnvironment.clearInherited start

    for pair: KeyValuePair<string, string> in environment do
        start.Environment[pair.Key] <- pair.Value

    BoundedProcess.run start input 8192 60000

let private shell directory script arguments environment input =
    runProcess
        directory
        "/bin/sh"
        ([ "-c"; script; "claimcore-output-control" ] @ arguments)
        environment
        input

let private pipe directory name =
    let path = Path.Combine(directory, name)
    let result = runProcess directory "mkfifo" [ "-m"; "600"; path ] (dict []) None

    if result.ExitCode <> 0 then
        invalidOp "Could not create the owned output-control FIFO."

    path

let private brokenOutput =
    "( : < \"$1\"; printf x > \"$2\" ) & exec 1>\"$1\"; while [ ! -f \"$2\" ]; do :; done; "

let private brokenErrors =
    "( : < \"$3\"; printf x > \"$4\" ) & exec 2>\"$3\"; while [ ! -f \"$4\" ]; do :; done; "

/// The FIFO reader closes before its completion marker; exec inherits a writer with no readers.
let run directory binary command destination environment source =
    Directory.CreateDirectory(directory) |> ignore
    let output = pipe directory "stdout.pipe"
    let errors = pipe directory "stderr.pipe"

    let arguments =
        [ output; output + ".closed"; errors; errors + ".closed"; binary; command ]

    let redirects =
        match destination with
        | "broken" -> brokenOutput
        | "both" -> brokenOutput + brokenErrors
        | "stderr-broken" -> brokenErrors
        | "full" when OperatingSystem.IsLinux() -> "exec 1>/dev/full; "
        | "pipe" -> ""
        | _ -> invalidArg (nameof destination) "Unknown output-control destination."

    shell directory (redirects + "exec dotnet \"$5\" \"$6\"") arguments environment (Some source)

let private quote (value: string) = "'" + value.Replace("'", "'\\''") + "'"

let terminal directory binary command =
    let errors = Path.Combine(directory, "terminal.stderr")

    let child =
        "umask 077; exec 2>"
        + quote errors
        + "; stty raw -echo; exec dotnet "
        + quote binary
        + " "
        + quote command

    let arguments =
        if OperatingSystem.IsLinux() then
            [ "-q"; "-e"; "-c"; child; "/dev/null" ]
        else
            [ "-q"; "/dev/null"; "/bin/sh"; "-c"; child ]

    runProcess directory "script" arguments (dict []) None

let file directory binary command mode (seed: byte array) =
    let path = Path.Combine(directory, "capture")
    File.WriteAllBytes(path, seed)
    File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)

    let redirects =
        match mode with
        | "truncate"
        | "shared" -> "exec 1>\"$1\"; "
        | "append"
        | "shared-append" -> "exec 1>>\"$1\"; "
        | "positioned" -> "exec 1<>\"$1\"; dd bs=1 count=\"$2\" <&1 >/dev/null 2>/dev/null; "
        | _ -> invalidArg (nameof mode) "Unknown file-control mode."

    let shared =
        if mode.StartsWith("shared", StringComparison.Ordinal) then
            "exec 2>&1; "
        else
            ""

    let position =
        if mode.Contains("append", StringComparison.Ordinal) then
            "zero-offset"
        else
            "any-offset"

    let arguments = [ path; string seed.Length; binary; command; position ]

    let result =
        shell
            directory
            (redirects + shared + "exec dotnet \"$3\" \"$4\" \"$5\"")
            arguments
            (dict [])
            None

    result, File.ReadAllBytes path

let regularFailure directory binary =
    let path = Path.Combine(directory, "failure.capture")

    if OperatingSystem.IsLinux() then
        let readiness = Path.Combine(directory, "failure.ready")
        let input = pipe directory "failure.input"

        let script =
            "umask 077; trap '' XFSZ; exec 3<>\"$3\"; "
            + "( while [ ! -s \"$2\" ]; do :; done; "
            + "prlimit --pid \"$$\" --fsize=0:0 && printf '*' >&3 ) & "
            + "exec 1>\"$1\"; exec 2>\"$2\"; exec dotnet \"$4\" file-failure <&3"

        let result =
            shell directory script [ path; readiness; input; binary ] (dict []) None

        if File.ReadAllText readiness <> "STREAM_READY\n" then
            invalidOp "The file-failure probe did not reach its managed output boundary."

        result
    else
        let script =
            "trap '' XFSZ; ulimit -f 0; exec 1>\"$1\"; exec dotnet \"$2\" file-failure"

        shell directory script [ path; binary ] (dict []) (Some [| 42uy |])
