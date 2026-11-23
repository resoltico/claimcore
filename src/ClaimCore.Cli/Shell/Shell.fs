namespace ClaimCore.Cli

open ClaimCore.CommandLine

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

[<NoEquality; NoComparison>]
type internal ShellConsole =
    {
        Read: bool -> string -> Task<ShellLine>
        Interrupt: unit -> unit
        Output: Stream
        Errors: Stream
        ReviewInteractive: bool
    }

/// The idle timer and input await share one read; expiry never leaves a second reader behind.
module internal ShellIdle =
    let timeout = TimeSpan.FromMinutes 15.

    let read (clock: TimeProvider) since (pending: Task<ShellLine>) discard =
        task {
            let elapsed = clock.GetElapsedTime since

            let remaining =
                if elapsed < TimeSpan.Zero then
                    TimeSpan.Zero
                else
                    max TimeSpan.Zero (timeout - elapsed)

            use stopTimer = new CancellationTokenSource()
            let timer = Task.Delay(remaining, clock, stopTimer.Token)
            let! finished = Task.WhenAny(pending :> Task, timer)

            if obj.ReferenceEquals(finished, timer) then
                do! discard ()
                return! pending
            else
                stopTimer.Cancel()
                return! pending
        }

/// Completed review lines are persistent output; only the unfinished question belongs to line editing.
module internal ShellReviewOutput =
    let create (output: Stream) =
        let pending = ResizeArray<byte>()

        let write (bytes: byte array) =
            for value in bytes do
                pending.Add value

                if value = 10uy then
                    output.Write(pending.ToArray())
                    output.Flush()
                    pending.Clear()

        let take () =
            let current = Encoding.UTF8.GetString(pending.ToArray())
            pending.Clear()
            current

        write, take

type internal ShellSession
    (clock: TimeProvider, console: ShellConsole, processor: RemoteFrameProcessor, ?saveAttachment) =
    let saveAttachment = defaultArg saveAttachment AttachmentFiles.save
    let gate = obj ()
    let mutable active: CancellationTokenSource option = None
    let mutable stopping = false
    let mutable promptInterrupts = 0
    let mutable lastActivity = clock.GetTimestamp()
    let mutable emptyInterrupt = false
    let mutable commandInterrupted = false
    let mutable interruptionCode: int option = None

    let reviewConsole =
        let write, question = ShellReviewOutput.create console.Errors

        {
            IsInteractive = console.ReviewInteractive
            Write = write
            ReadLine =
                fun () ->
                    let pending = console.Read false (question ())

                    let line =
                        ShellIdle.read clock (clock.GetTimestamp()) pending (fun () ->
                            processor.DiscardAuthentication CancellationToken.None)
                        |> _.GetAwaiter().GetResult()

                    match line with
                    | ShellLine.Text text -> Some text
                    | _ -> None
        }

    let verbConsole =
        {
            Output = console.Output
            Errors = console.Errors
            Review = reviewConsole
        }

    let dispatch words admitted (token: CancellationToken) =
        task {
            token.ThrowIfCancellationRequested()

            let unsupported issue =
                CliDiscovery.unsupported
                    console.Errors
                    (CliInvocations.usageLanguage words)
                    words
                    issue
                |> VerbCompletion.Completed

            match admitted with
            | Ok(CliInvocation.Verb arguments) ->
                return!
                    VerbRunner.runWithCompletion
                        saveAttachment
                        verbConsole
                        processor
                        arguments
                        token
            | Ok(CliInvocation.Configure None) ->
                return unsupported (UsageError.NotApplicable("--show", "shell"))
            | Ok(CliInvocation.Call | CliInvocation.Batch | CliInvocation.Shell | CliInvocation.Recovery _ | CliInvocation.Configure _) ->
                return unsupported (UsageError.NotApplicable("command", "shell"))
            | Ok invocation ->
                return
                    CliDiscovery.discovery console.Output console.Errors invocation
                    |> VerbCompletion.Completed
            | Error issue -> return unsupported issue
        }

    let execute words =
        task {
            use cancellation = new CancellationTokenSource()
            let admitted = CliInvocations.admitConfigured words

            let language =
                match admitted with
                | Ok(CliInvocation.Verb arguments) -> CliInvocations.usageLanguage arguments
                | Ok(CliInvocation.Help(_, rendering, _) | CliInvocation.CommandHelp(_, rendering, _)) ->
                    rendering.Language
                | Ok(CliInvocation.Recovery selected) -> selected.Language
                | _ -> CliInvocations.usageLanguage words

            lock gate (fun () ->
                processor.BeginCommand language
                active <- Some cancellation
                commandInterrupted <- false
                interruptionCode <- None

                if promptInterrupts > 0 then
                    promptInterrupts <- promptInterrupts - 1
                    commandInterrupted <- true
                    interruptionCode <- Some(ExitCode.value ExitCode.Cancelled)
                    cancellation.Cancel())

            try
                try
                    let! completion = dispatch words admitted cancellation.Token

                    match completion with
                    | VerbCompletion.Completed _ when cancellation.IsCancellationRequested ->
                        return
                            (interruptionCode |> Option.defaultWith processor.Interrupt), language
                    | _ -> return VerbCompletion.exitCode completion, language
                with error ->
                    return processor.Failure error, language
            finally
                lock gate (fun () -> active <- None)
                lastActivity <- clock.GetTimestamp()
        }

    let status language code =
        MessageCatalogs.renderCli language (CliMessage.ShellExitCode(string code))
        |> fun text -> CliDiscovery.writeText console.Errors (text + Environment.NewLine)

    let submitted text =
        task {
            emptyInterrupt <- false

            match ShellTokenizer.split text with
            | Ok [] -> return None
            | Ok [ "exit" ] -> return Some 0
            | Ok words ->
                let! code, language = execute words
                emptyInterrupt <- commandInterrupted
                status language code
                return None
            | Error _ ->
                let language = CliLanguage.effective ()

                CliDiscovery.writeText
                    console.Errors
                    (MessageCatalogs.renderCli language CliMessage.InvalidShellLine
                     + Environment.NewLine)

                status language (ExitCode.value ExitCode.Usage)
                lastActivity <- clock.GetTimestamp()
                return None
        }

    let received line =
        task {
            match line with
            | ShellLine.End -> return Some 0
            | ShellLine.Interrupted hadText ->
                lock gate (fun () -> promptInterrupts <- max 0 (promptInterrupts - 1))

                if emptyInterrupt && not hadText then
                    return Some(ExitCode.value ExitCode.Cancelled)
                else
                    emptyInterrupt <- true
                    return None
            | ShellLine.Text text -> return! submitted text
        }

    member _.Interrupt() =
        lock gate (fun () ->
            match active with
            | Some command ->
                commandInterrupted <- true
                let code = processor.Interrupt()

                if interruptionCode <> Some(ExitCode.value ExitCode.OutcomeUnknown) then
                    interruptionCode <- Some code

                command.Cancel()
            | None -> promptInterrupts <- promptInterrupts + 1

            console.Interrupt())

    member this.Stop() =
        stopping <- true
        this.Interrupt()

    member _.Run() =
        task {
            let mutable result = None

            try
                try
                    while result.IsNone && not stopping do
                        let pending =
                            lock gate (fun () ->
                                if promptInterrupts > 0 then
                                    Task.FromResult(ShellLine.Interrupted false)
                                else
                                    console.Read true "claimcore> ")

                        let! line =
                            ShellIdle.read clock lastActivity pending (fun () ->
                                processor.DiscardAuthentication CancellationToken.None)

                        let! next = received line
                        result <- next

                    return
                        if stopping then
                            ExitCode.value ExitCode.Cancelled
                        else
                            defaultArg result 0
                with error ->
                    return processor.Failure error
            finally
                processor.DiscardAuthentication CancellationToken.None
                |> _.GetAwaiter().GetResult()
        }
