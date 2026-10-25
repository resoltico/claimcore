module internal ClaimCore.AcceptanceTests.CliInvocationEvidence

open ClaimCore.Cli

open System.IO
open System.Text
open ClaimCore.TestSupport
open ClaimCore.Contract
open ClaimCore.Contract.Protocol

let private stages =
    set
        [
            "start"
            "driver-started"
            "browser-launched"
            "context-created"
            "page-created"
            "authorization-navigation-started"
            "authorization-opened"
            "login-form-missing"
            "username-filled"
            "password-filled"
            "login-submitted"
            "callback-observed"
            "wrong-state-delivered"
        ]

let private safeStage (source: string) =
    let value = source.Trim()
    let prefix = "driver-refused-after-"

    if
        value = "unknown"
        || value = "unavailable"
        || value = "not-applicable"
        || Set.contains value stages
    then
        value
    elif value.StartsWith(prefix, System.StringComparison.Ordinal) then
        let stage = value.Substring(prefix.Length)

        if Set.contains stage stages then
            prefix + stage
        else
            "unknown"
    else
        "unknown"

let private readText maximum path =
    try
        use source =
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

        let length = source.Length

        if length > maximum then
            Error "unknown"
        else
            let bytes = Array.zeroCreate<byte>(int length)
            source.ReadExactly(bytes)
            UTF8Encoding(false, true).GetString bytes |> Ok
    with
    | :? IOException
    | :? System.UnauthorizedAccessException
    | :? DecoderFallbackException -> Error "unavailable"

let internal readStage path =
    match readText 128L path with
    | Ok value -> safeStage value
    | Error category -> category

let internal browserReturn progress =
    progress
    |> Option.bind (fun path -> readText 512L (path + ".return") |> Result.toOption)
    |> Option.bind CliBrowserReturnEvidence.decode
    |> Option.defaultValue "unavailable"

let private replyCount (source: byte array) =
    source
    |> Array.indexed
    |> Array.fold
        (fun (start, count) (index, value) ->
            if value = byte '\n' then
                let valid = Frames.decode source[start .. index - 1] |> Result.isOk
                index + 1, count + (if valid then 1 else 0)
            else
                start, count)
        (0, 0)
    |> snd

let private captured stage output errors =
    $"CLI invocation evidence: authStage={safeStage stage}; validReplyFrames={replyCount output}; stdoutBytes={output.Length}; stderrBytes={Array.length errors}"

let internal describe stage (failure: BoundedProcessFailure) =
    captured stage failure.StandardOutput failure.StandardError
    + $"; kind={failure.Kind}; cleanupFailed={failure.CleanupFailed}."

let private report (diagnostics: TextWriter) progress render =
    try
        let stage = progress |> Option.map readStage |> Option.defaultValue "not-applicable"
        diagnostics.WriteLine(render stage + " browserReturn=" + browserReturn progress)
    with
    | :? IOException
    | :? System.ObjectDisposedException -> ()

let internal run expected diagnostics progress (invoke: unit -> ProcessRunner.Result) =
    try
        let result = invoke ()

        if result.ExitCode <> expected then
            report diagnostics progress (fun stage ->
                captured stage result.StandardOutput result.StandardError
                + $"; exitCode={result.ExitCode}.")

        result
    with :? BoundedProcessFailure as failure ->
        report diagnostics progress (fun stage -> describe stage failure)
        reraise ()
