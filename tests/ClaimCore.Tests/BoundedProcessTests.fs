module ClaimCore.Tests.BoundedProcessTests

open System
open System.Diagnostics
open System.IO
open System.Text
open Expecto
open ClaimCore.TestSupport

let private run script (input: byte array option) bound timeout =
    let start = ProcessStartInfo("node")
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.RedirectStandardInput <- input.IsSome
    start.ArgumentList.Add("--input-type=module")
    start.ArgumentList.Add("-e")
    start.ArgumentList.Add(script)
    BoundedProcess.run start input bound timeout

let private healthy () =
    let result =
        run
            "process.stdin.resume(); process.stdin.on('end',()=>{process.stdout.write('ok');process.stderr.write('note')})"
            (Some [| 1uy |])
            32
            5000

    Expect.equal result.ExitCode 0 "Finite child completed"
    Expect.equal (Encoding.UTF8.GetString result.StandardOutput) "ok" "Exact bounded stdout"
    Expect.equal (Encoding.UTF8.GetString result.StandardError) "note" "Exact bounded stderr"

let private excessiveOutput () =
    for channel in [ "stdout"; "stderr" ] do
        Expect.throwsT<InvalidOperationException>
            (fun () ->
                run
                    ($"process.{channel}.write('x'.repeat(8192));setInterval(()=>{{}},1000)")
                    None
                    32
                    5000
                |> ignore)
            "An oversized stream is stopped before unbounded accumulation"

let private blockedInput () =
    let start = Stopwatch.StartNew()

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            run
                "process.stdin.pause();setInterval(()=>{},1000)"
                (Some(Array.zeroCreate<byte>(4 * 1024 * 1024)))
                32
                300
            |> ignore)
        "A child that never reads input cannot evade the process deadline"

    Expect.isLessThan
        start.Elapsed.TotalSeconds
        5.
        "Deadline includes input delivery and termination"

let tests =
    testList
        "bounded test processes"
        [
            testCase "test process concurrently captures finite input and output" healthy
            testCase "test process stops oversized output on either channel" excessiveOutput
            testCase "test process deadline covers a blocked input pipe" blockedInput
        ]
