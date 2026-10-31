module ClaimCore.Tests.DatabaseTimingTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Protocol
open ClaimCore.Database

let private field name value =
    JsonValue.tryMember name value
    |> Option.defaultWith (fun () -> failtestf "Missing result fact: %s" name)

[<Tests>]
let tests =
    testCase "verification results retain elapsed milliseconds and outcome meaning" (fun () ->
        let cases =
            [
                (VerificationResult.Verified LedgerPosition.genesis, "VERIFIED", 0)
                (VerificationResult.Diverged
                    {
                        Sequence = None
                        Finding = VerificationFinding.Head
                    },
                 "DIVERGED",
                 5)
            ]

        for result, kind, expectedCode in cases do
            let bytes, code = Replies.verification 23L result
            Expect.equal code expectedCode "Timing does not change the verification exit"

            let value =
                StrictJson.read Limits.MaximumResponseBytes bytes
                |> Result.defaultWith (fun _ -> failtest "Invalid database result JSON")

            Expect.equal
                (field "kind" value)
                (JsonValue.String kind)
                "The original verification outcome remains explicit"

            Expect.equal
                (field "elapsedMilliseconds" value)
                (JsonValue.Integer 23L)
                "Elapsed time is a numeric result fact"

            Expect.isOk
                (Codec.decode Limits.MaximumResponseBytes Replies.codec bytes)
                "The timed result follows its actual codec")
