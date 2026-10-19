module internal ClaimCore.AcceptanceTests.CliDriverCompletion

open System
open System.Diagnostics
open System.IO
open System.Threading

let private receipt path =
    try
        use stream =
            new FileStream(path + ".closed", FileMode.Open, FileAccess.Read, FileShare.ReadWrite)

        let length = stream.Length

        if length > 12L then
            "invalid"
        else
            let bytes = Array.zeroCreate<byte>(int length)
            stream.ReadExactly bytes

            match Text.Encoding.ASCII.GetString bytes with
            | "closed\n" -> "closed"
            | "not-started\n" -> "not-started"
            | "failed\n" -> "failed"
            | _ -> "invalid"
    with
    | :? FileNotFoundException -> "pending"
    | :? IOException
    | :? UnauthorizedAccessException -> "unavailable"

let private awaitReceipt budget (timer: Stopwatch) path =
    let mutable state = receipt path

    while state = "pending" && timer.ElapsedMilliseconds < int64 budget do
        Thread.Sleep(min 20 (max 1 (budget - int timer.ElapsedMilliseconds)))
        state <- receipt path

    if state = "pending" then "deadline" else state

let private report (diagnostics: TextWriter) state =
    try
        diagnostics.WriteLine($"CLI browser cleanup evidence: completion={state}.")
    with
    | :? IOException
    | :? ObjectDisposedException -> ()

let private complete budget timer diagnostics progress required successful =
    match progress with
    | None -> ()
    | Some path ->
        let launched = File.Exists path

        let state =
            if launched then
                awaitReceipt budget timer path
            else
                "not-launched"

        let accepted = state = "closed" || (state = "not-started" && not successful)

        if not accepted && (launched || successful) then
            report diagnostics state

            if required then
                invalidOp "The synthetic CLI browser did not complete its owned cleanup."

let private secondary budget timer diagnostics progress =
    try
        complete budget timer diagnostics progress false false
    with _ ->
        ()

let run budget diagnostics progress expected (invoke: unit -> ProcessRunner.Result) =
    let timer = Stopwatch.StartNew()

    let result =
        try
            invoke ()
        with _ ->
            secondary budget timer diagnostics progress
            reraise ()

    if result.ExitCode = expected then
        complete budget timer diagnostics progress true (result.ExitCode = 0)
    else
        secondary budget timer diagnostics progress

    result
