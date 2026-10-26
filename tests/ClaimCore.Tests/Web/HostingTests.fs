module ClaimCore.Tests.HostingTests

open ClaimCore.Contract
open ClaimCore.Domain

open System
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Web.Composition

[<Tests>]
let tests =
    testList
        "runtime lifetime"
        [
            testCase "shutdown closes admission and disposes exactly once" (fun () ->
                let mutable closes = 0
                let gate = RuntimeLeases(fun () -> closes <- closes + 1)
                let lease = gate.Acquire() |> Option.defaultWith (fun () -> failtest "Admission")
                let stopping = gate.Stop()
                Expect.isFalse stopping.IsCompleted "Admitted work is pending"
                Expect.isNone (gate.Acquire()) "No further admission"
                lease.Dispose()
                lease.Dispose()
                gate.Stop().GetAwaiter().GetResult()
                Expect.equal closes 1 "Source closes once")
            testCaseAsync
                "bound expiry returns while admitted work retains the source until drain"
                (async {
                    let mutable closes = 0
                    let gate = RuntimeLeases(fun () -> closes <- closes + 1)

                    use lease =
                        gate.Acquire() |> Option.defaultWith (fun () -> failtest "Admission")

                    let! outcome = gate.Shutdown(TimeSpan.Zero) |> Async.AwaitTask
                    Expect.equal outcome ShutdownOutcome.BoundExpired "Bound is effective"
                    Expect.equal closes 0 "Active leases retain the source after the waiting bound"

                    Expect.isFalse
                        (gate.Stop().IsCompleted)
                        "Drain remains pending while admitted work is active"

                    Expect.isNone (gate.Acquire()) "Admission stays closed"
                    lease.Dispose()
                    let! drained = gate.Shutdown(TimeSpan.FromSeconds 1.) |> Async.AwaitTask
                    Expect.equal drained ShutdownOutcome.Drained "Work finishes later"
                    Expect.equal closes 1 "No second close"
                })
            testCaseAsync
                "shutdown preserves an admitted accepted result"
                (async {
                    let _, _, _, receipt = DecisionFixtures.created 490 "LIFETIME"

                    let expected: ExecuteReply =
                        {
                            OperationId = receipt.OperationId
                            Outcome = ExecuteResult.Accepted receipt
                        }

                    let gate = RuntimeLeases(ignore)
                    let mutable factoryCalls = 0

                    let call action =
                        ActorAccess.run
                            gate
                            (fun _ ->
                                factoryCalls <- factoryCalls + 1
                                ())
                            DecisionFixtures.owner
                            action

                    let finish =
                        TaskCompletionSource<ExecuteReply>(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        )

                    let running = call (fun () -> finish.Task)
                    let stopping = gate.Shutdown(TimeSpan.FromSeconds 2.)

                    let! refused = call (fun () -> Task.FromResult()) |> Async.AwaitTask

                    Expect.isNone refused "New calls never acquire an actor"
                    Expect.equal factoryCalls 1 "Closed admission never invokes the actor factory"

                    finish.SetResult expected
                    let! outcome = running |> Async.AwaitTask

                    Expect.equal outcome (Some expected) "Admitted acceptance survives shutdown"

                    let! shutdown = stopping |> Async.AwaitTask
                    Expect.equal shutdown ShutdownOutcome.Drained "Admitted work drained"
                })
            testCase
                "calendar derives dates from the captured instant and the stored zone"
                (fun () ->
                    let zone = BusinessCalendar.resolve "Europe/Riga"
                    let instant = DateTimeOffset(2026, 10, 13, 22, 30, 0, TimeSpan.Zero)

                    Expect.equal
                        (zone.DateAt instant)
                        (DateOnly(2026, 10, 14))
                        "Stored zone crosses UTC midnight"

                    Expect.equal zone.TimeZoneId "Europe/Riga" "Zone retained")
            testCase "shutdown bounds must be finite and nonnegative" (fun () ->
                let gate = RuntimeLeases(ignore)

                for bound in [ TimeSpan.FromMilliseconds(-1.); TimeSpan.MaxValue ] do
                    Expect.throws
                        (fun () -> gate.Shutdown(bound).GetAwaiter().GetResult() |> ignore)
                        "Invalid drain bound")
        ]
