module ClaimCore.Tests.OutcomeTests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json

[<Tests>]
let tests =
    testList
        "shared public outcomes"
        [
            testCase "common outcomes retain exit and repair semantics" (fun () ->
                for outcome, number, action in
                    [
                        Outcome.Succeeded(), 0, RecommendedAction.NoneRequired
                        Outcome.Rejected Refusal.InvalidCursor, 2, RecommendedAction.ReadCurrent
                        Outcome.Failed CoreFault.StoreUnavailable, 3, RecommendedAction.RetrySafe
                    ] do
                    Expect.equal
                        ((OutcomePresentation.classify outcome).ExitCode |> ExitCode.value)
                        number
                        "Public exit commitment"

                    Expect.equal
                        (OutcomePresentation.recommendedAction outcome)
                        action
                        "Repair action follows the same common outcome")
            testCase
                "unknown execute delivery retains its separate wire tag and exit four"
                (fun () ->
                    let operationId = Guid.Parse "10000000-0000-4000-8000-000000000001"

                    let value =
                        {
                            ExecuteReply.OperationId = operationId
                            Outcome = ExecuteResult.OutcomeUnknown
                        }

                    let encoded = Codec.encode ReplyCodecs.execute value |> Encoding.UTF8.GetString

                    Expect.equal
                        encoded
                        """{"operationId":"10000000-0000-4000-8000-000000000001","outcome":{"kind":"OUTCOME_UNKNOWN","data":null}}"""
                        "Unknown delivery is never encoded as a known failure"

                    Expect.equal
                        ((OutcomePresentation.classifyExecute value.Outcome).ExitCode
                         |> ExitCode.value)
                        4
                        "Unknown delivery keeps its recovery exit"

                    Expect.equal
                        ((OutcomePresentation.classifyExecute (
                            ExecuteResult.Failed CoreFault.StoreUnavailable
                         ))
                             .ExitCode
                         |> ExitCode.value)
                        3
                        "A known failure has a different exit")
        ]
