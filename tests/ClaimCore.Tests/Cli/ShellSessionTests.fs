module ClaimCore.Tests.ShellSessionTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
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
                    (endpoint.Output.Write(
                        ResponseExamples.forRepresentation (Frames.representation frame) endpoint
                    ))
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
                    ShellLine.Text "help exit-codes"
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
        Expect.equal invoked 2 "Offline help topics do not dispatch"
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
            "case close --outcome other REF --expected-revision 1 --operation-id 10000000-0000-7000-8000-000000000001 --format json"

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
            "operation get 10000000-0000-7000-8000-000000000001"
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

type private ObservedOutput(onWrite: unit -> unit) =
    inherit MemoryStream()

    override _.Write(bytes: ReadOnlySpan<byte>) =
        onWrite ()
        base.Write bytes

let private interruptAroundPublication () =
    task {
        let fixture =
            Environment.GetEnvironmentVariable "CLAIMCORE_ATTACHMENT_TEST_NO_HARDLINK_DIRECTORY"
            |> Option.ofObj

        let directory =
            match fixture with
            | None -> Directory.CreateTempSubdirectory "claimcore-shell-download-"
            | Some path ->
                Directory.CreateDirectory(Path.Combine(path, Guid.NewGuid().ToString("N")))

        try
            let bytes = [| 0x61uy; 0uy; 0xffuy |]
            let before = Path.Combine(directory.FullName, "before.bin")
            let after = Path.Combine(directory.FullName, "after.bin")

            let download target =
                ShellLine.Text(
                    "attachment download REF 018f785e-1b45-7cc4-b9c9-cebf5f897689 --output "
                    + CommandGrammar.quoteArgument target
                )

            use terminal =
                new Terminal([ download before; download after; ShellLine.Text "context" ])

            let signal () =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let incoming, returnBytes, laterRead = signal (), signal (), signal ()
            let mutable transfers = 0
            let mutable interrupt = ignore
            let mutable observedSave = false

            use output =
                new ObservedOutput(fun () ->
                    if fixture.IsNone then
                        // The real file publication has returned. Interrupt before its status reaches the shell.
                        Expect.equal
                            (File.ReadAllBytes after)
                            bytes
                            "The published file is complete."

                        observedSave <- true
                        interrupt ())

            let save path buffer cancelled =
                AttachmentFiles.saveWith
                    (fun output bytes ->
                        Expect.isSome fixture "The fixture triggers actual exclusive creation."
                        Expect.isTrue (File.Exists after) "The exclusive destination exists."
                        Expect.equal output.Length 0L "Interrupt precedes the content write."
                        observedSave <- true
                        interrupt ()
                        output.Write bytes
                        output.Flush(true))
                    path
                    buffer
                    cancelled

            let receive _ _ =
                task {
                    transfers <- transfers + 1

                    if transfers = 1 then
                        incoming.SetResult()
                        do! returnBytes.Task.WaitAsync(TimeSpan.FromSeconds 5.)

                    return Ok bytes
                }

            let invoke _ beforeSend cancelled =
                task {
                    beforeSend ()
                    laterRead.SetResult()
                    do! Task.Delay(Timeout.Infinite, cancelled)
                    return CliReply.local ClientFailure.Unavailable
                }

            use processor =
                RemoteFrameProcessor.ForTest(
                    Stream.Null,
                    output,
                    terminal.Errors,
                    invoke,
                    noDiscard,
                    download = receive
                )

            let shell =
                ShellSession(
                    Clock(),
                    { terminal.Console with
                        Output = output
                    },
                    processor,
                    saveAttachment = save
                )

            interrupt <- shell.Interrupt

            let running = shell.Run()
            do! incoming.Task.WaitAsync(TimeSpan.FromSeconds 5.)
            shell.Interrupt()
            returnBytes.SetResult()
            do! laterRead.Task.WaitAsync(TimeSpan.FromSeconds 5.)
            shell.Interrupt()
            do! terminal.AtRead 4
            terminal.Submit(ShellLine.Text "exit")
            let! _ = running.WaitAsync(TimeSpan.FromSeconds 5.)

            Expect.isFalse (File.Exists before) "Interrupt before save creates no output."
            Expect.isTrue observedSave "The publication race occurred before status delivery."

            Expect.equal
                (File.ReadAllBytes after)
                bytes
                "Interrupt after save preserves exact bytes."

            Expect.stringContains
                (text output)
                "Attachment saved."
                "A completed save remains visible."

            let statuses =
                (text terminal.Errors).Split('\n')
                |> Array.map _.Trim()
                |> Array.filter (fun line ->
                    line.StartsWith("Exit code:", StringComparison.Ordinal))
                |> Array.toList

            Expect.equal
                statuses
                [ "Exit code: 130"; "Exit code: 0"; "Exit code: 130" ]
                "Cancellation applies before publication and to the following command."
        finally
            directory.Delete true
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
                        "case close --outcome other REF --expected-revision 1 --operation-id 10000000-0000-7000-8000-000000000001 --format json"
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

let private downloadPreviewRefusal () =
    task {
        let directory = Directory.CreateTempSubdirectory "claimcore-shell-preview-refusal-"

        try
            let path = Path.Combine(directory.FullName, "attachment.pdf")

            let command =
                "attachment download REF 10000000-0000-7000-8000-000000000001 --output "
                + CommandGrammar.quoteArgument path

            use terminal = new Terminal([ ShellLine.Text command; ShellLine.Text "exit" ])

            let download _ _ =
                Refusal.AttachmentPreviewUnavailable
                |> HumanReplies.rejection
                |> AttachmentTransferFailure.Rejected
                |> AttachmentDownloadFailure.Refused
                |> Error
                |> Task.FromResult

            use processor =
                RemoteFrameProcessor.ForTest(
                    Stream.Null,
                    terminal.Output,
                    terminal.Errors,
                    (fun _ _ _ -> failtest "A binary read cannot use framed JSON execution."),
                    noDiscard,
                    download = download
                )

            let shell = ShellSession(Clock(), terminal.Console, processor)
            let! _ = shell.Run()
            let errors = text terminal.Errors

            Expect.stringContains
                errors
                "This attachment cannot be previewed."
                "Core refusal wording"

            Expect.stringContains
                errors
                "Exit code: 2"
                "Binary refusal uses the ordinary rejection exit"

            Expect.isFalse (File.Exists path) "A rejected transfer never publishes bytes"
        finally
            directory.Delete true
    }

[<Tests>]
let tests =
    testList
        "interactive shell session"
        [
            testCase "queries share sign-in and exit or EOF discard it" runSignInAndExit
            testTask "a typed attachment preview refusal maps to shell rejection without output" {
                do! downloadPreviewRefusal ()
            }
            testTask "uncertain delivery preserves retry and the shell continues" {
                do! uncertainContinues ()
            }
            testTask "Ctrl-C cancels the command and a second at the prompt exits" {
                do! interruptCommand ()
            }
            testTask
                "Ctrl-C reports cancellation before attachment publication and success after it" {
                do! interruptAroundPublication ()
            }
            testTask "Ctrl-C clears prompt input before a second exits" { do! interruptPrompt () }
            testTask "consecutive prompt interrupts cannot disappear between reads" {
                do! consecutiveInterrupts ()
            }
            testTask "status-output failure preserves mutation uncertainty and retry" {
                do! failedStatusRetainsUncertainty ()
            }
        ]


[<Tests>]
let reviewOutput =
    testList
        "shell review output and editable questions"
        [
            testCase
                "fragmented writes retain persistent scope once and one incomplete question"
                (fun () ->
                    use persistent = new MemoryStream()
                    let write, question = ShellReviewOutput.create persistent
                    write (Encoding.UTF8.GetBytes "Account scope.\nOperation identity.\n")
                    write (Encoding.UTF8.GetBytes "Type ")
                    write (Encoding.UTF8.GetBytes "the exact target: ")

                    Expect.equal
                        (text persistent)
                        "Account scope.\nOperation identity.\n"
                        "Completed scope lines are visible before a read"

                    Expect.equal
                        (question ())
                        "Type the exact target: "
                        "The editor owns only the complete single-line question"

                    Expect.equal
                        (question ())
                        ""
                        "A consumed question cannot repeat on another read"

                    write (Encoding.UTF8.GetBytes "Not recorded.\n")

                    Expect.equal
                        (text persistent)
                        "Account scope.\nOperation identity.\nNot recorded.\n"
                        "Decline feedback is visible without another confirmation read")
            testCase
                "single-byte UTF-8 fragments preserve Latvian and Arabic scope and question"
                (fun () ->
                    use persistent = new MemoryStream()
                    let write, question = ShellReviewOutput.create persistent
                    let scope = "Konts: Ārija.\nالحساب: ليلى.\n"
                    let prompt = "Apstiprināt / تأكيد: "

                    for value in Encoding.UTF8.GetBytes(scope + prompt) do
                        write [| value |]

                    Expect.equal
                        (persistent.ToArray())
                        (Encoding.UTF8.GetBytes scope)
                        "Completed lines preserve exact UTF-8 bytes"

                    Expect.equal
                        (question ())
                        prompt
                        "A fragmented question is decoded only when complete"

                    Expect.equal (question ()) "" "The question is consumed once")
        ]
