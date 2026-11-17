module ClaimCore.Tests.MaintenanceFencingTests

open System
open Expecto
open ClaimCore.Application.Contracts
open ClaimCore.Domain

let private first = Guid.Parse "10000000-0000-4000-8000-000000000001"
let private second = Guid.Parse "10000000-0000-4000-8000-000000000002"

let private accepted state change =
    MaintenanceChange.apply state change |> Option.get

[<Tests>]
let tests =
    testList
        "maintenance generation fencing"
        [
            testCase "acquire retry and released acquire cannot create another pause" (fun () ->
                let request = MaintenanceChange.Acquire(0L, first)
                let paused = accepted RegisterMaintenance.initial request
                Expect.equal paused.Generation 1L "First acquisition advances the generation"

                Expect.equal
                    (MaintenanceChange.apply paused request)
                    (Some paused)
                    "Same active request is retryable"

                let handle = paused.Handle |> Option.get
                let resumed = accepted paused (MaintenanceChange.Resume handle)
                Expect.equal resumed.Generation 2L "Resume advances the generation"
                Expect.isFalse resumed.Active "No active pause"
                Expect.isNone (MaintenanceChange.apply resumed request) "Delayed acquire is stale"

                Expect.isNone
                    (MaintenanceChange.apply resumed (MaintenanceChange.Resume handle))
                    "Released handle cannot resume again")
            testCase "takeover fences old resume and competing acquisitions" (fun () ->
                let paused =
                    accepted RegisterMaintenance.initial (MaintenanceChange.Acquire(0L, first))

                let old = paused.Handle |> Option.get

                Expect.isNone
                    (MaintenanceChange.apply paused (MaintenanceChange.Acquire(0L, second)))
                    "Another operator cannot steal the active pause"

                let replaced = accepted paused (MaintenanceChange.Takeover(old, second))
                Expect.equal replaced.Generation 2L "Takeover advances the generation"

                Expect.isNone
                    (MaintenanceChange.apply replaced (MaintenanceChange.Resume old))
                    "Old resume is fenced"

                Expect.isNone
                    (MaintenanceChange.apply replaced (MaintenanceChange.Takeover(old, first)))
                    "Old takeover is fenced"

                Expect.isFalse
                    (accepted replaced (MaintenanceChange.Resume(replaced.Handle |> Option.get)))
                        .Active
                    "Current handle can resume")
            testCase
                "every final-generation acquisition or takeover retains a matching resume"
                (fun () ->
                    let last = SafeInteger.Maximum
                    let inactive = RegisterMaintenance.create (last - 2L) None
                    let request = MaintenanceChange.Acquire(last - 2L, first)
                    let paused = accepted inactive request
                    Expect.equal paused.Generation (last - 1L) "Last releasable acquisition"

                    Expect.equal
                        (MaintenanceChange.apply paused request)
                        (Some paused)
                        "Same active retry"

                    let resumed =
                        accepted paused (MaintenanceChange.Resume(paused.Handle |> Option.get))

                    Expect.equal resumed.Generation last "Final resume advances monotonically"
                    Expect.isFalse resumed.Active "Final state is inactive"

                    Expect.isNone
                        (MaintenanceChange.apply
                            (RegisterMaintenance.create (last - 1L) None)
                            (MaintenanceChange.Acquire(last - 1L, first)))
                        "No unreleasable acquisition"

                    let current = RegisterMaintenance.create (last - 2L) (Some first)

                    let taken =
                        accepted
                            current
                            (MaintenanceChange.Takeover(current.Handle |> Option.get, second))

                    Expect.isFalse
                        (accepted taken (MaintenanceChange.Resume(taken.Handle |> Option.get)))
                            .Active
                        "Final takeover can resume"

                    Expect.isNone
                        (MaintenanceChange.apply
                            paused
                            (MaintenanceChange.Takeover(paused.Handle |> Option.get, second)))
                        "Takeover cannot consume the reserved resume generation")
        ]
