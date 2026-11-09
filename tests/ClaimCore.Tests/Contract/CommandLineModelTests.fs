module ClaimCore.Tests.CommandLineModelTests

open ClaimCore.TestSupport

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.Database

[<Tests>]
let tests =
    testList
        "shared command-line models"
        [
            testCase "CLI mapped result exits stay in its supported subset" (fun () ->
                let outcomes =
                    [
                        Outcome.Succeeded(), ExitCode.Success
                        Outcome.Rejected Refusal.InvalidCursor, ExitCode.Rejected
                        Outcome.Failed CoreFault.StoreUnavailable, ExitCode.Failed
                    ]

                for outcome, expected in outcomes do
                    let actual = (OutcomePresentation.classify outcome).ExitCode
                    Expect.equal actual expected "Shared outcome exit meaning"

                    Expect.contains
                        CliIdentity.supportedExitCodes
                        actual
                        "CLI declares each mapped result"

                Expect.contains
                    CliIdentity.supportedExitCodes
                    (OutcomePresentation.classifyExecute PresentedExecuteResult.OutcomeUnknown)
                        .ExitCode
                    "Unknown delivery is supported"

                for code in [ ExitCode.Usage; ExitCode.InternalError; ExitCode.Cancelled ] do
                    Expect.contains CliIdentity.supportedExitCodes code "Local exits are declared")
            testCase "database mapped result exits stay in its supported subset" (fun () ->
                let position = LedgerPosition.genesis

                let successes =
                    [
                        DatabaseSuccess.Initialized(
                            Guid.Parse "10000000-0000-7000-8000-000000000001"
                        )
                        DatabaseSuccess.Head position
                        DatabaseSuccess.Rebuilt
                            {
                                Position = position
                                PreviousDivergence = None
                            }
                        DatabaseSuccess.RestoredSessionsInvalidated 3L
                        DatabaseSuccess.Verified(position, 12L)
                    ]

                let replies =
                    (successes |> List.map (Outcome.Succeeded >> Reply.Operation))
                    @ [
                        Reply.Operation(Outcome.Rejected Refusal.InvalidCursor)
                        Reply.Operation(Outcome.Failed CoreFault.StoreUnavailable)
                        Reply.Cancelled
                        Reply.OutcomeUnknown None
                        Reply.Diverged(
                            {
                                Sequence = None
                                Finding = VerificationFinding.Head
                            },
                            12L
                        )
                        Reply.Usage
                            {
                                Issue = UsageError.MissingOperand "TIME_ZONE"
                                Usage = []
                            }
                        Reply.Help { Acts = []; ExitCodes = [] }
                        Reply.Version
                            {
                                Product = "ClaimCore"
                                Version = "0"
                                AssemblyVersion = "0"
                                FileVersion = "0"
                            }
                    ]

                for reply in replies do
                    Expect.contains
                        Command.supportedExitCodes
                        (Replies.exitCode reply)
                        "Every mapped database result has a local declaration"

                Expect.equal
                    (Replies.exitCode (Reply.OutcomeUnknown None) |> ExitCode.value)
                    4
                    "Unknown commit remains distinct")
        ]
