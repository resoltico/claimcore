module ClaimCore.Tests.HostingTests

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.TestSupport.StoreContractTests

let private calendar = BusinessCalendar.resolve "Etc/UTC"

let private fixture (store: MemoryLedgerStore.Store) : Fixture =
    {
        Store = store :> ILedgerStore
        InstallationId = id 500
        Owner =
            PrincipalKey.human "https://issuer.example/" "owner"
            |> Result.defaultWith failtest
        Calendar = calendar
        Dispose = ignore
    }

[<Tests>]
let tests =
    testList
        "runtime lifetime"
        [
            testCase "[CC-RUN-001] shutdown closes admission and disposes exactly once" (fun () ->
                let mutable closes = 0
                let gate = AdmissionGate(fun () -> closes <- closes + 1)
                let lease = gate.Acquire() |> Option.defaultWith (fun () -> failtest "Admission")
                let stopping = gate.Stop()
                Expect.isFalse stopping.IsCompleted "Admitted work is pending"
                Expect.isNone (gate.Acquire()) "No further admission"
                lease.Dispose()
                lease.Dispose()
                gate.Stop().GetAwaiter().GetResult()
                Expect.equal closes 1 "Source closes once")
            testCaseAsync
                "bound expiry closes the source once without releasing or cancelling admitted work"
                (async {
                    let mutable closes = 0
                    let gate = AdmissionGate(fun () -> closes <- closes + 1)

                    use lease =
                        gate.Acquire() |> Option.defaultWith (fun () -> failtest "Admission")

                    let! outcome = gate.Shutdown(TimeSpan.Zero) |> Async.AwaitTask
                    Expect.equal outcome ShutdownOutcome.BoundExpired "Bound is effective"
                    Expect.equal closes 1 "Source closes at bound"
                    Expect.isNone (gate.Acquire()) "Admission stays closed"
                    lease.Dispose()
                    let! drained = gate.Shutdown(TimeSpan.FromSeconds 1.) |> Async.AwaitTask
                    Expect.equal drained ShutdownOutcome.Drained "Work finishes later"
                    Expect.equal closes 1 "No second close"
                })
            testCaseAsync
                "shutdown preserves an admitted accepted result"
                (async {
                    use store =
                        new MemoryLedgerStore.Store(
                            DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)
                        )

                    let fixture = fixture store
                    let! core = caseWorker fixture |> Async.AwaitTask
                    let gate = AdmissionGate(ignore)
                    let actor = ActorFacade.bind gate core

                    let entered =
                        TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

                    use finish = new ManualResetEventSlim(false)

                    store.AfterCommit <-
                        fun () ->
                            entered.TrySetResult() |> ignore

                            if not (finish.Wait(TimeSpan.FromSeconds 3.)) then
                                failtest "Release missing"

                    let running =
                        Task.Run<ExecuteOutcome>(
                            Func<Task<ExecuteOutcome>>(fun () ->
                                actor.Execute(
                                    request
                                        10
                                        "synthetic-lifetime"
                                        0L
                                        (ClaimCore.Contract.CaseAct.Open registration),
                                    CancellationToken.None
                                ))
                        )

                    do! entered.Task |> Async.AwaitTask
                    let stopping = gate.Shutdown(TimeSpan.FromSeconds 2.)
                    let! refused = actor.Context CancellationToken.None |> Async.AwaitTask

                    Expect.equal
                        refused
                        (ReadReply.Failed CoreFault.StoreUnavailable)
                        "New calls refuse"

                    finish.Set()
                    let! outcome = running |> Async.AwaitTask
                    accepted outcome |> ignore
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
                let gate = AdmissionGate(ignore)

                for bound in [ TimeSpan.FromMilliseconds(-1.); TimeSpan.MaxValue ] do
                    Expect.throws
                        (fun () -> gate.Shutdown(bound).GetAwaiter().GetResult() |> ignore)
                        "Invalid drain bound")
        ]
