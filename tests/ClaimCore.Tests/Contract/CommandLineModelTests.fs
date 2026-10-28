module ClaimCore.Tests.CommandLineModelTests

open ClaimCore.TestSupport

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
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
                    (OutcomePresentation.classifyExecute ExecuteResult.OutcomeUnknown).ExitCode
                    "Unknown delivery is supported"

                for code in [ ExitCode.Usage; ExitCode.InternalError; ExitCode.Cancelled ] do
                    Expect.contains CliIdentity.supportedExitCodes code "Local exits are declared")
            testCase "database mapped result exits stay in its supported subset" (fun () ->
                let position = LedgerPosition.genesis

                let accepted =
                    {
                        OwnerAcceptance.OperationId =
                            (CaseFixtures.operationId (
                                Guid.Parse "10000000-0000-4000-8000-000000000001"
                            ))
                        TargetActorId =
                            (CaseFixtures.actorId (
                                Guid.Parse "10000000-0000-4000-8000-000000000002"
                            ))
                        Position = position
                    }

                let successes =
                    [
                        DatabaseSuccess.Initialized(OperationId.value accepted.OperationId)
                        DatabaseSuccess.Head position
                        DatabaseSuccess.Accepted accepted
                        DatabaseSuccess.AlreadyAccepted accepted
                        DatabaseSuccess.Verified position
                    ]

                let replies =
                    (successes |> List.map (Outcome.Succeeded >> Reply.Operation))
                    @ [
                        Reply.Operation(Outcome.Rejected Refusal.InvalidCursor)
                        Reply.Operation(Outcome.Failed CoreFault.StoreUnavailable)
                        Reply.Cancelled
                        Reply.OutcomeUnknown None
                        Reply.Diverged
                            {
                                Sequence = None
                                Finding = VerificationFinding.Head
                            }
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
