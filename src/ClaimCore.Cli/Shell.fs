namespace ClaimCore.Cli

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Protocol

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

type internal ShellSession
    (clock: TimeProvider, console: ShellConsole, processor: RemoteFrameProcessor) =
    let gate = obj ()
    let mutable active: CancellationTokenSource option = None
    let mutable stopping = false
    let mutable promptInterrupts = 0
    let mutable lastActivity = clock.GetTimestamp()
    let mutable emptyInterrupt = false
    let mutable commandInterrupted = false
    let mutable interruptionCode: int option = None

    let reviewConsole =
        let mutable prompt = ""

        {
            IsInteractive = console.ReviewInteractive
            Write = fun bytes -> prompt <- Encoding.UTF8.GetString bytes
            ReadLine =
                fun () ->
                    let pending = console.Read false prompt

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
            WriteSettlementPrompt = reviewConsole.Write
        }

    let dispatch words (token: CancellationToken) =
        task {
            token.ThrowIfCancellationRequested()

            match CliInvocations.admit words with
            | Ok(CliInvocation.Verb arguments) ->
                return! VerbRunner.run verbConsole processor arguments token
            | Ok(CliInvocation.Call | CliInvocation.Session | CliInvocation.Shell | CliInvocation.Recovery _ | CliInvocation.Configure _) ->
                return
                    CliDiscovery.unsupported
                        console.Errors
                        "en"
                        words
                        (UsageError.NotApplicable("command", "shell"))
            | Ok invocation ->
                return CliDiscovery.discovery console.Output console.Errors invocation
            | Error issue ->
                return
                    CliDiscovery.unsupported
                        console.Errors
                        (CliInvocations.usageLanguage words)
                        words
                        issue
        }

    let execute words =
        task {
            use cancellation = new CancellationTokenSource()
            let language = CliInvocations.usageLanguage words

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
                    let! code = dispatch words cancellation.Token

                    if cancellation.IsCancellationRequested then
                        return interruptionCode |> Option.defaultWith processor.Interrupt
                    else
                        return code
                with error ->
                    return processor.Failure error
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
                let! code = execute words
                emptyInterrupt <- commandInterrupted
                status (CliInvocations.usageLanguage words) code
                return None
            | Error _ ->
                CliDiscovery.writeText
                    console.Errors
                    (MessageCatalogs.renderCli "en" CliMessage.InvalidShellLine
                     + Environment.NewLine)

                status "en" (ExitCode.value ExitCode.Usage)
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
