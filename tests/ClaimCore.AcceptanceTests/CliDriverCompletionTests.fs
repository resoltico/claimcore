module ClaimCore.AcceptanceTests.CliDriverCompletionTests

open ClaimCore.Contract

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Expecto
open ClaimCore.TestSupport

let private result code : ProcessRunner.Result =
    {
        ExitCode = code
        StandardOutput = [||]
        StandardError = [||]
    }

let private withProgress action =
    let directory = FixtureFiles.directory ()

    try
        action (Path.Combine(directory, "progress"))
    finally
        Directory.Delete(directory, true)

let private delayedCompletion () =
    withProgress (fun progress ->
        File.WriteAllText(progress, "callback-observed")

        let closed =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let closing =
            task {
                do! closed.Task
                do! Task.Delay 50
                File.WriteAllText(progress + ".closed.tmp", "closed\n")
                File.Move(progress + ".closed.tmp", progress + ".closed")
            }

        let expected = result 0

        let actual =
            CliDriverCompletion.run 3000 TextWriter.Null (Some progress) 0 (fun () ->
                closed.SetResult()
                expected)

        try
            Expect.equal
                (File.ReadAllText(progress + ".closed"))
                "closed\n"
                "The next invocation waits for actual driver cleanup"
        finally
            closing.GetAwaiter().GetResult()

        Expect.isTrue
            (obj.ReferenceEquals(actual, expected))
            "The original successful result survives")

let private incompleteCompletion () =
    withProgress (fun progress ->
        for receipt in
            [
                None
                Some "failed\n"
                Some "not-started\n"
                Some "private-marker"
                Some(String('x', 13))
            ] do
            File.WriteAllText(progress, "callback-observed")

            receipt
            |> Option.iter (fun value -> File.WriteAllText(progress + ".closed", value))

            use diagnostics = new StringWriter()

            Expect.throws
                (fun () ->
                    CliDriverCompletion.run 20 diagnostics (Some progress) 0 (fun () -> result 0)
                    |> ignore)
                "A successful authenticated result cannot hide absent or failed cleanup"

            Expect.isFalse
                (diagnostics.ToString().Contains "private-marker")
                "Receipt contents stay private"

            File.Delete(progress + ".closed")

        File.Delete progress

        Expect.throws
            (fun () ->
                CliDriverCompletion.run 3000 TextWriter.Null (Some progress) 0 (fun () ->
                    result 0)
                |> ignore)
            "Interactive success requires evidence that the driver actually started")

let private noLaunchRefusal () =
    withProgress (fun progress ->
        let refused = result 3
        let timer = Stopwatch.StartNew()

        let actual =
            CliDriverCompletion.run 3000 TextWriter.Null (Some progress) 3 (fun () -> refused)

        Expect.isTrue
            (obj.ReferenceEquals(actual, refused))
            "Pre-launch refusal retains its result"

        Expect.isLessThan
            timer.ElapsedMilliseconds
            1000L
            "An absent driver requires no cleanup wait"

        File.WriteAllText(progress, "driver-started")
        File.WriteAllText(progress + ".closed", "not-started\n")

        let withoutBrowser =
            CliDriverCompletion.run 3000 TextWriter.Null (Some progress) 3 (fun () -> refused)

        Expect.isTrue
            (obj.ReferenceEquals(withoutBrowser, refused))
            "A driver that never obtained a browser has no browser cleanup outstanding")

let private retainedFailures () =
    withProgress (fun progress ->
        File.WriteAllText(progress, "driver-started")
        File.WriteAllText(progress + ".closed", "failed\n")
        let refused = result 3

        let actual =
            CliDriverCompletion.run 100 TextWriter.Null (Some progress) 0 (fun () -> refused)

        Expect.isTrue
            (obj.ReferenceEquals(actual, refused))
            "Cleanup failure preserves an unexpected result"

        let failure = BoundedProcessFailure(Deadline, [||], [||], false)
        let mutable observed = None

        try
            CliDriverCompletion.run 1 TextWriter.Null (Some progress) 0 (fun () -> raise failure)
            |> ignore
        with error ->
            observed <- Some error

        Expect.isTrue
            (observed |> Option.exists (fun error -> obj.ReferenceEquals(error, failure)))
            "Cleanup failure preserves the identical original process exception"

        File.Delete(progress + ".closed")
        let elapsed = Stopwatch.StartNew()
        use diagnostics = new StringWriter()

        let afterDeadline =
            CliDriverCompletion.run 0 diagnostics (Some progress) 0 (fun () -> refused)

        Expect.isTrue
            (obj.ReferenceEquals(afterDeadline, refused))
            "The expired original budget is retained"

        Expect.stringContains
            (diagnostics.ToString())
            "completion=deadline"
            "Exhaustion is explicit"

        Expect.isLessThan
            elapsed.ElapsedMilliseconds
            1000L
            "Cleanup never starts another timeout budget")

[<Tests>]
let tests =
    testList
        "published CLI browser ownership"
        [
            testCase "driver cleanup completes before another invocation" delayedCompletion
            testCase
                "successful authentication requires bounded closed cleanup evidence"
                incompleteCompletion
            testCase "pre-launch refusal does not wait for a nonexistent driver" noLaunchRefusal
            testCase "cleanup failure preserves the original result and exception" retainedFailures
        ]
