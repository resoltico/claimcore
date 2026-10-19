module internal ClaimCore.AcceptanceTests.CliInvocationEvidence

open System.IO
open System.Text
open ClaimCore.TestSupport
open ClaimCore.Wire

let private stages =
    set
        [
            "start"
            "driver-started"
            "browser-launched"
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

let private readStage path =
    try
        use source =
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

        let length = source.Length

        if length > 128L then
            "unknown"
        else
            let bytes = Array.zeroCreate<byte>(int length)
            source.ReadExactly(bytes)
            UTF8Encoding(false, true).GetString bytes |> safeStage
    with
    | :? IOException
    | :? System.UnauthorizedAccessException
    | :? DecoderFallbackException -> "unavailable"

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

let internal describe stage (failure: BoundedProcessFailure) =
    $"CLI invocation evidence: authStage={safeStage stage}; validReplyFrames={replyCount failure.StandardOutput}; stdoutBytes={failure.StandardOutput.Length}; stderrBytes={failure.StandardError.Length}; kind={failure.Kind}; cleanupFailed={failure.CleanupFailed}."

let internal run (diagnostics: TextWriter) progress invoke =
    try
        invoke ()
    with :? BoundedProcessFailure as failure ->
        try
            let stage = progress |> Option.map readStage |> Option.defaultValue "not-applicable"
            diagnostics.WriteLine(describe stage failure)
        with
        | :? IOException
        | :? System.ObjectDisposedException -> ()

        reraise ()
