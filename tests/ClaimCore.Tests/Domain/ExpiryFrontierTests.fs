module ClaimCore.Tests.ExpiryFrontierTests

open System
open Expecto
open ClaimCore.Domain

let private issued = DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero)

let private identity =
    Guid.CreateVersion7(issued) |> OperationId.create |> Option.get

[<Tests>]
let tests =
    testList
        "durable expiry precision"
        [
            testCase
                "frontier equality stays executable and adjacent ticks expire strictly"
                (fun () ->
                    Expect.isFalse
                        (OperationLifetime.expiredBefore None identity)
                        "No confirmed expiry"

                    Expect.isFalse
                        (OperationLifetime.expiredBefore (Some issued) identity)
                        "Equality is not expired"

                    Expect.isFalse
                        (OperationLifetime.expiredBefore (Some(issued.AddTicks -1L)) identity)
                        "One tick before issuance"

                    Expect.isTrue
                        (OperationLifetime.expiredBefore (Some(issued.AddTicks 1L)) identity)
                        "One tick after issuance cannot round down to UUID milliseconds")
            testCase "cutoff retains precision and survives a later clock rollback" (fun () ->
                let observed = issued.AddMinutes(10).AddTicks(1)
                let cutoff = OperationLifetime.cutoff observed
                Expect.equal cutoff (issued.AddTicks 1L) "Full observed precision"
                let first = OperationLifetime.advanceFrontier None cutoff

                let rollback =
                    OperationLifetime.advanceFrontier first (OperationLifetime.cutoff issued)

                Expect.equal rollback first "Backward wall time cannot resurrect the identity"

                Expect.isTrue
                    (OperationLifetime.expiredBefore rollback identity)
                    "Expiry is monotonic"

                Expect.equal
                    (OperationLifetime.cutoff DateTimeOffset.MinValue)
                    DateTimeOffset.MinValue
                    "No underflow at supported boundary")
            testCase
                "checkpoint preserves prefix and full cutoff independently of display offset"
                (fun () ->
                    let installation = Guid.Parse "10000000-0000-4000-8000-000000000001"
                    let cutoff = issued.AddTicks(1).ToOffset(TimeSpan.FromHours(2))

                    let checkpoint =
                        InstallationCheckpoint.create
                            installation
                            LedgerPosition.genesis
                            (Some cutoff)
                        |> Option.get

                    Expect.equal
                        checkpoint.Position
                        LedgerPosition.genesis
                        "Prefix stays separate from expiry"

                    Expect.equal
                        checkpoint.ExpiredBefore
                        (Some(issued.AddTicks 1L))
                        "No precision loss"

                    Expect.equal
                        (checkpoint.ExpiredBefore |> Option.get).Offset
                        TimeSpan.Zero
                        "Canonical UTC"

                    Expect.isNone
                        (InstallationCheckpoint.create Guid.Empty LedgerPosition.genesis None)
                        "Installation binding is mandatory")
        ]
