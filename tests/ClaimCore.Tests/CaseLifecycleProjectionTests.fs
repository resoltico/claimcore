module ClaimCore.Tests.CaseLifecycleProjectionTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Tests.Fixtures
open ClaimCore.Tests.CaseLifecycleTestSupport

let private snapshot = opened () |> Claim.view

let private hold =
    {
        Id = Guid.Parse "30000000-0000-4000-8000-000000000001"
        Ground = "Synthetic review ground"
        ReviewOn = DateOnly(2026, 9, 20)
        RecordedBy = Guid.Parse "30000000-0000-4000-8000-000000000002"
        RecordedAt = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
    }

let private strictProjection =
    testCase
        "[CC-LIFE-001] lifecycle SQL projection loader refuses duplicate and terminal holds"
        (fun _ ->
            let caseId = Guid.NewGuid()

            match
                CaseLifecycle.restoreProjection
                    caseId
                    snapshot
                    CaseDisposition.VoidedDataEntryError
                    PrivacyPhase.ErasureRequested
                    [ hold ]
            with
            | Ok value ->
                Expect.equal
                    (CaseLifecycle.disposition value)
                    CaseDisposition.VoidedDataEntryError
                    "Disposition is orthogonal"

                Expect.equal
                    (CaseLifecycle.privacy value)
                    PrivacyPhase.ErasureRequested
                    "Privacy phase is orthogonal"
            | Error _ -> failtest "Valid synthetic projection must restore."

            Expect.equal
                (CaseLifecycle.restoreProjection
                    caseId
                    snapshot
                    CaseDisposition.Active
                    PrivacyPhase.Active
                    [ hold; hold ])
                (Error LifecycleRefusal.InvalidIdentity)
                "Duplicate hold IDs cannot be projected"

            Expect.equal
                (CaseLifecycle.restoreProjection
                    caseId
                    snapshot
                    CaseDisposition.Active
                    PrivacyPhase.ErasureFinal
                    [ hold ])
                (Error LifecycleRefusal.HoldActive)
                "Terminal erasure cannot retain an active hold")

let private boundedActiveHolds =
    testCase "[CC-LIFE-001] a case has at most 256 simultaneous active holds" (fun _ ->
        let mutable current =
            CaseLifecycle.initial (Guid.NewGuid()) snapshot
            |> Result.defaultWith (fun _ -> failwith "Synthetic case is invalid.")

        for _ in 1..256 do
            current <-
                CaseLifecycle.recordHold current { hold with Id = Guid.NewGuid() }
                |> Result.defaultWith (fun _ -> failwith "A permitted hold was rejected.")

        Expect.equal (CaseLifecycle.holds current).Length 256 "Exact capacity is allowed"

        Expect.equal
            (CaseLifecycle.recordHold current { hold with Id = Guid.NewGuid() })
            (Error LifecycleRefusal.HoldCapacityExceeded)
            "The next hold is refused before persistence")

let tests = testList "lifecycle projection" [ strictProjection; boundedActiveHolds ]
