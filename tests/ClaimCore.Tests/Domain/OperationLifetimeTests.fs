module ClaimCore.Tests.OperationLifetimeTests

open System
open Expecto
open ClaimCore.Domain

let private operation =
    Guid.Parse "019aa6a0-0000-7000-8000-000000000001"
    |> OperationId.create
    |> Option.defaultWith (fun () -> failtest "The RFC UUIDv7 control must parse")

let private issued =
    DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64("019aa6a00000", 16))

[<Tests>]
let tests =
    testList
        "bounded UUIDv7 operation identities"
        [
            testCase "only RFC version 7 with the IETF variant is admitted" (fun () ->
                for source in
                    [
                        "019aa6a0-0000-4000-8000-000000000001"
                        "019aa6a0-0000-7000-0000-000000000001"
                        "019aa6a0-0000-7000-c000-000000000001"
                        "00000000-0000-0000-0000-000000000000"
                    ] do
                    Expect.isNone (OperationId.create (Guid.Parse source)) source

                Expect.isSome (OperationId.create (Guid.CreateVersion7())) "A BCL UUIDv7 parses")
            testCase "the first six network-order bytes are Unix milliseconds" (fun () ->
                Expect.equal
                    (OperationId.issuedAt operation)
                    issued
                    "Independent hexadecimal timestamp"

                Expect.equal
                    (OperationLifetime.finalAt operation)
                    (issued.AddMinutes 10.)
                    "Ten-minute deadline")
            testCase
                "expiry is inclusive and refuses the first later provider microsecond"
                (fun () ->
                    Expect.isTrue
                        (OperationLifetime.admits (issued.AddMinutes 10.) operation)
                        "At expiry"

                    Expect.isFalse
                        (OperationLifetime.admits (issued.AddMinutes(10.).AddTicks 10L) operation)
                        "One microsecond late")
            testCase
                "future allowance is inclusive and refuses the first excess microsecond"
                (fun () ->
                    Expect.isTrue
                        (OperationLifetime.admits (issued.AddMinutes -2.) operation)
                        "At future allowance"

                    Expect.isFalse
                        (OperationLifetime.admits (issued.AddMinutes(-2.).AddTicks -10L) operation)
                        "One microsecond too far ahead")
        ]
