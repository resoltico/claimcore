module ClaimCore.DocsTests.ProcessCaptureTests

open System
open System.IO
open Expecto
open ClaimCore.Docs

let private run script timeout =
    (SystemProcessRunner() :> IProcessRunner).Run
        {
            FileName = "node"
            Arguments = [ "-e"; script ]
            WorkingDirectory = Path.GetTempPath()
            Environment = []
            Timeout = timeout
        }

let private boundary excess exit =
    let count = 16 * 1024 * 1024 + excess

    let result =
        run
            ($"process.stdout.write(Buffer.alloc({count},65)); process.exitCode={exit};")
            (TimeSpan.FromSeconds 15.)

    match excess, result with
    | 0, Ok output ->
        Expect.equal output.ExitCode exit "Natural execution status is preserved"
        Expect.equal output.StandardOutput.Length count "Exact byte allowance is retained"
    | _, Error diagnostic ->
        Expect.stringContains
            diagnostic
            ("child exit " + string exit)
            "Overflow does not hide execution status"
    | _ -> failtest "Over-limit output qualified as a successful delivery."

let private bothStreams () =
    let half = 8 * 1024 * 1024

    match
        run
            ($"process.stdout.write(Buffer.alloc({half})); process.stderr.write(Buffer.alloc({half}));")
            (TimeSpan.FromSeconds 15.)
    with
    | Ok output ->
        Expect.equal output.StandardOutput.Length half "Stdout shares the allowance"
        Expect.equal output.StandardError.Length half "Stderr shares the allowance"
    | Error _ -> failtest "Exact combined byte allowance was refused."

let private splitUnicode () =
    match
        run
            "process.stdout.write(Buffer.from([226])); setImmediate(()=>process.stdout.write(Buffer.from([130,172])));"
            (TimeSpan.FromSeconds 5.)
    with
    | Ok output -> Expect.equal output.StandardOutput "€" "Decode only after raw-byte capture"
    | Error _ -> failtest "Split UTF-8 capture failed."

let private stalled () =
    let elapsed = Diagnostics.Stopwatch.StartNew()

    match run "setInterval(()=>{},1000);" (TimeSpan.FromMilliseconds 100.) with
    | Error diagnostic ->
        Expect.stringContains diagnostic "exit " "Deadline retains execution outcome"
    | Ok _ -> failtest "Stalled child qualified."

    Expect.isLessThan
        elapsed.Elapsed.TotalSeconds
        5.
        "Termination and readers settle within finite cleanup"

let tests =
    testList
        "native documentation console capture"
        [
            testCase "exact console byte allowance retains natural failure" (fun () -> boundary 0 7)
            testCase "one excess byte refuses an otherwise successful child" (fun () ->
                boundary 1 0)
            testCase "console overflow preserves natural failure" (fun () -> boundary 1 7)
            testCase "console allowance is combined across actual stdout and stderr" bothStreams
            testCase "raw-byte capture preserves split UTF-8" splitUnicode
            testCase "stalled console child settles termination and pipe cleanup" stalled
        ]
