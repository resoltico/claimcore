module ClaimCore.Tests.ShellReviewIdleTests

open System
open System.IO
open System.Threading.Tasks
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol
open ClaimCore.TestSupport
open Expecto
open ShellConsoleFixture
open ShellIdentityFixture
open ShellTestClock

let private response frame (endpoint: Endpoint<'Input, 'Output>) value =
    CliReply.result frame (endpoint.Output.Write value)
    |> Result.defaultWith (fun _ -> failtest "Typed review response must be admitted.")

let private reviewed frame =
    let current = CliReplyFixtures.snapshot

    let review =
        OperationReview.Case
            {
                Before = Some current
                Proposed = current
                Changes = []
                Context =
                    {
                        ProductVersion = "synthetic"
                        EffectiveBusinessDate = DateOnly(2026, 10, 1)
                        TimeZoneId = "Etc/UTC"
                    }
            }

    response
        frame
        Api.operationPreview
        {
            OperationId = Frames.operationId frame |> Option.get
            Outcome = Outcome.Succeeded(Previewed.Reviewed review)
        }

let private idleConfirmation () =
    task {
        let clock = Clock()
        use provider = new Provider(clock)
        use credentials = session provider
        use terminal = new Terminal([ ShellLine.Text "case close x" ])

        let invoke frame dispatched cancelled =
            task {
                let! access = credentials.Token cancelled
                Expect.isOk access "Review requests authenticate normally"
                dispatched ()

                if (Frames.endpoint frame).Id = Api.caseGet.Id then
                    return
                        response
                            frame
                            Api.caseGet
                            (Outcome.Succeeded
                                {
                                    AssignedToYou = false
                                    Snapshot = CliReplyFixtures.snapshot
                                    Advice = CaseAdvice.create [] None [] |> Fixtures.accepted
                                })
                else
                    return reviewed frame
            }

        let discarded =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let discard cancelled =
            task {
                do! credentials.Discard cancelled
                discarded.TrySetResult() |> ignore
            }

        use processor =
            RemoteFrameProcessor.ForTest(
                Stream.Null,
                terminal.Output,
                terminal.Errors,
                invoke,
                discard
            )

        let shell = ShellSession(clock, terminal.Console, processor)
        let running = shell.Run()
        do! terminal.AtRead 2
        do! (clock.WhenTimer 2).WaitAsync(TimeSpan.FromSeconds 5.)
        Expect.equal provider.SignIns 1 "Lookup and preview share sign-in"
        clock.Advance(TimeSpan.FromMinutes 15.)
        do! discarded.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        terminal.Submit(ShellLine.Text "n")
        do! terminal.AtRead 3
        token credentials |> ignore

        Expect.equal
            provider.SignIns
            2
            "Idle confirmation discards refresh material before the next token request"

        Expect.equal provider.Refreshes 0 "Idle confirmation cannot refresh the old grant"
        terminal.Submit ShellLine.End
        let! code = running.WaitAsync(TimeSpan.FromSeconds 5.)
        Expect.equal code 0 "Declining review and EOF remain safe"
        do! provider.Completed()
    }

[<Tests>]
let tests =
    testTask "shell confirmation prompt also applies the fifteen-minute idle lock" {
        do! idleConfirmation ()
    }
