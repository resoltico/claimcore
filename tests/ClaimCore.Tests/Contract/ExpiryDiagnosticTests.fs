module ClaimCore.Tests.ExpiryDiagnosticTests

open System
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.Presentation

[<Tests>]
let tests =
    testCase
        "frontier diagnostic preserves precise evidence and operational guidance in both representations"
        (fun () ->
            let cutoff = DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero).AddTicks 1L
            let observed = cutoff.AddTicks(-1L)
            let refusal = Refusal.OperationClockUnavailable(cutoff, observed)
            let bytes = Codec.encode DiagnosticCodecs.diagnostic refusal

            Expect.equal
                (Codec.decode 4096 DiagnosticCodecs.diagnostic bytes)
                (Ok refusal)
                "Exact full-precision refusal"

            use json = JsonDocument.Parse bytes
            let root = json.RootElement

            Expect.equal
                (root.GetProperty("code").GetString())
                "OPERATION_CLOCK_UNAVAILABLE"
                "Dedicated operational code"

            Expect.equal
                (root.GetProperty("phase").GetString())
                "NOT_STARTED"
                "No dispatch/commit ambiguity is hidden"

            Expect.equal
                (root.GetProperty("action").GetString())
                "STOP_AND_INVESTIGATE"
                "Do not blame malformed input or advise a new identity"

            Expect.equal
                (root.GetProperty("expiredBefore").GetString())
                "2026-11-09T12:00:00.0000001Z"
                "Cutoff is not rounded to UUID milliseconds"

            match HumanReplies.rejection refusal with
            | HumanRejection.Refused advice ->
                Expect.equal
                    advice.Action
                    RecommendedAction.StopAndInvestigate
                    "Same operational action"

                Expect.equal
                    advice.Message.MessageId
                    "diagnostic.OPERATION_CLOCK_UNAVAILABLE"
                    "Authored shared message"

                Expect.equal
                    advice.Message.Arguments["expiredBefore"]
                    (PresentedValue.Text "2026-11-09T12:00:00.0000001Z")
                    "Human evidence preserves all cutoff ticks"
            | other -> failtestf "Expected operational human refusal: %A" other)
