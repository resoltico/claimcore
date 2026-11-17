module ClaimCore.Tests.MaintenanceFencingTests

open System
open Expecto
open ClaimCore.Application.Contracts

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
        ]
