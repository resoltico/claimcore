module ClaimCore.Tests.ShellSessionTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.TestSupport
open Expecto
open ShellConsoleFixture
open ShellIdentityFixture
open ShellTestClock

let private positive frame =
    let definition =
        Api.definitions |> List.find (fun item -> item.Id = (Frames.endpoint frame).Id)

    definition.Accept
        { new IEndpointVisitor<CliReply> with
            member _.Visit<'Input, 'Output>(endpoint: Endpoint<'Input, 'Output>) =
                CliReply.result
                    frame
                    (endpoint.Output.Write(ResponseExamples.defaultReply endpoint))
                |> Result.defaultWith (fun _ -> failtest "Positive endpoint reply refused.")
        }

let private text (stream: MemoryStream) =
    Encoding.UTF8.GetString(stream.ToArray())

let private noDiscard _ = Task.FromResult()

let runSignInAndExit () =
    for ending in [ ShellLine.Text "exit"; ShellLine.End ] do
        let clock = Clock()
        use provider = new Provider(clock)
        use credentials = session provider

        use terminal =
            new Terminal(
                [
                    ShellLine.Text "help"
                    ShellLine.Text "describe"
                    ShellLine.Text "context"
                    ShellLine.Text "case list"
                    ending
                ]
            )

        let mutable invoked = 0

        let invoke frame dispatched cancelled =
            task {
                let! access = credentials.Token cancelled
                Expect.isOk access "Authentication succeeds through real code flow"
                invoked <- invoked + 1
                dispatched ()
                return positive frame
            }

        use processor =
            RemoteFrameProcessor.ForTest(
                Stream.Null,
                terminal.Output,
                terminal.Errors,
                invoke,
                credentials.Discard
            )

        let shell = ShellSession(clock, terminal.Console, processor)

        let exitCode =
            shell.Run().WaitAsync(TimeSpan.FromSeconds 8.).GetAwaiter().GetResult()

        Expect.equal exitCode 0 "Exit and EOF both leave normally"
        Expect.equal invoked 2 "Offline help and describe do not dispatch"
        Expect.equal provider.SignIns 1 "Queries in one shell share one PKCE sign-in"

        Expect.equal
            ((text terminal.Errors).Split("Exit code: 0").Length)
            5
            "Every command prints its exit code"

        token credentials |> ignore
        Expect.equal provider.SignIns 2 "Exit and EOF discard access and refresh material"
        provider.Completed().GetAwaiter().GetResult()

let private uncertainContinues () =
    task {
        let clock = Clock()

        let mutation =
            "case close REF --expected-revision 1 --operation-id 10000000-0000-4000-8000-000000000001 --format json"

        use terminal =
            new Terminal(
                [ ShellLine.Text mutation; ShellLine.Text "context"; ShellLine.Text "exit" ]
            )

        let requests = ResizeArray<Invocation>()

        let invoke frame dispatched _ =
            requests.Add frame
            dispatched ()

            Task.FromResult(
                if requests.Count = 1 then
                    CliReply.unknown (CliReply.local ClientFailure.Unavailable)
                else
                    positive frame
            )

        use processor =
            RemoteFrameProcessor.ForTest(
                Stream.Null,
                terminal.Output,
                terminal.Errors,
                invoke,
                noDiscard
            )

        let shell = ShellSession(clock, terminal.Console, processor)
        let! code = shell.Run()
        Expect.equal code 0 "User can exit after resolving subsequent work"
        Expect.equal requests.Count 2 "Uncertain command does not stop the interactive shell"
        Expect.stringContains (text terminal.Errors) "Exit code: 4" "Uncertainty remains visible"
        Expect.stringContains (text terminal.Errors) "Exit code: 0" "Following read completes"

        Expect.stringContains
            (text terminal.Errors)
            (Encoding.UTF8.GetString(Frames.encodeInvocation requests[0]))
            "Frozen retry frame is exact"

        Expect.stringContains
            (text terminal.Errors)
            "operation status 10000000-0000-4000-8000-000000000001"
            "Passive status guidance retains the exact operation ID"
    }

let private interruptCommand () =
    task {
        let clock = Clock()
        use terminal = new Terminal([ ShellLine.Text "context" ])

        let dispatched =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let invoke _ beforeSend cancelled =
            task {
                beforeSend ()
                dispatched.TrySetResult() |> ignore
                do! Task.Delay(Timeout.Infinite, cancelled)
                return CliReply.local ClientFailure.Unavailable
            }

        use processor =
            RemoteFrameProcessor.ForTest(
                Stream.Null,
                terminal.Output,
                terminal.Errors,
                invoke,
                noDiscard
            )

        let shell = ShellSession(clock, terminal.Console, processor)
        let running = shell.Run()
        do! dispatched.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        shell.Interrupt()
        do! terminal.AtRead 2
        Expect.isFalse running.IsCompleted "First Ctrl-C returns to the prompt"

        Expect.stringContains
            (text terminal.Errors)
            "Exit code: 130"
            "Cancelled read reports its exit"

        shell.Interrupt()
        let! code = running.WaitAsync(TimeSpan.FromSeconds 5.)
        Expect.equal code 130 "Second Ctrl-C at the empty prompt exits"
    }

let private interruptPrompt () =
    task {
        let clock = Clock()
        use terminal = new Terminal([])

        use processor =
            RemoteFrameProcessor.ForTest(
                Stream.Null,
                terminal.Output,
                terminal.Errors,
                (fun _ _ _ -> Task.FromResult(CliReply.local ClientFailure.Unavailable)),
                noDiscard
            )

        let shell = ShellSession(clock, terminal.Console, processor)
        let running = shell.Run()
        do! terminal.AtRead 1
        terminal.BufferedText true
        shell.Interrupt()
        do! terminal.AtRead 2
        Expect.isFalse running.IsCompleted "First Ctrl-C clears buffered input"
        shell.Interrupt()
        let! code = running.WaitAsync(TimeSpan.FromSeconds 5.)
        Expect.equal code 130 "Second Ctrl-C at an empty prompt exits"
    }

let private consecutiveInterrupts () =
    task {
        let clock = Clock()
        use terminal = new Terminal([])

        use processor =
            RemoteFrameProcessor.ForTest(
                Stream.Null,
                terminal.Output,
                terminal.Errors,
                (fun _ _ _ -> Task.FromResult(CliReply.local ClientFailure.Unavailable)),
                noDiscard
            )

        let shell = ShellSession(clock, terminal.Console, processor)
        let running = shell.Run()
        do! terminal.AtRead 1
        shell.Interrupt()
        shell.Interrupt()
        let! code = running.WaitAsync(TimeSpan.FromSeconds 5.)
        Expect.equal code 130 "A signal between input awaits remains queued"
    }

type private StatusFailureErrors() =
    inherit MemoryStream()

    override _.Write(bytes: ReadOnlySpan<byte>) =
        if Encoding.UTF8.GetString(bytes).StartsWith("Exit code:", StringComparison.Ordinal) then
            raise (IOException "Synthetic status sink failure.")

        base.Write bytes

let private failedStatusRetainsUncertainty () =
    task {
        let clock = Clock()

        use terminal =
            new Terminal(
                [
                    ShellLine.Text
                        "case close REF --expected-revision 1 --operation-id 10000000-0000-4000-8000-000000000001 --format json"
                ]
            )

        use errors = new StatusFailureErrors()
        let mutable frozen = ""

        let invoke frame dispatched _ =
            frozen <- Encoding.UTF8.GetString(Frames.encodeInvocation frame)
            dispatched ()
            Task.FromResult(CliReply.unknown (CliReply.local ClientFailure.Unavailable))

        use processor =
            RemoteFrameProcessor.ForTest(Stream.Null, terminal.Output, errors, invoke, noDiscard)

        let shell =
            ShellSession(
                clock,
                { terminal.Console with
                    Errors = errors
                },
                processor
            )

        let! code = shell.Run()

        Expect.equal
            code
            4
            "Status-output failure cannot turn uncertain mutation delivery into an internal-error exit"

        Expect.stringContains
            (text errors)
            frozen
            "The exact frozen retry frame survives status failure"
    }

[<Tests>]
let tests =
    testList
        "interactive shell session"
        [
            testCase "queries share sign-in and exit or EOF discard it" runSignInAndExit
            testTask "uncertain delivery preserves retry and the shell continues" {
                do! uncertainContinues ()
            }
            testTask "Ctrl-C cancels the command and a second at the prompt exits" {
                do! interruptCommand ()
            }
            testTask "Ctrl-C clears prompt input before a second exits" { do! interruptPrompt () }
            testTask "consecutive prompt interrupts cannot disappear between reads" {
                do! consecutiveInterrupts ()
            }
            testTask "status-output failure preserves mutation uncertainty and retry" {
                do! failedStatusRetainsUncertainty ()
            }
        ]
