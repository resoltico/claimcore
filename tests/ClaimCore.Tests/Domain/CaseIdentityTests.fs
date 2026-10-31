module ClaimCore.Tests.CaseIdentityTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testCase "OPEN identity is its operation UUID and is retained across later changes" (fun () ->
        let first = Guid.Parse "91000000-0000-4000-8000-000000000001"
        let second = Guid.Parse "91000000-0000-4000-8000-000000000002"

        let openCase id =
            CaseFixtures.decide
                (DateOnly(2026, 9, 7))
                {
                    OperationId = id
                    CaseReference = "Immutable-Reference"
                    ExpectedRevision = 0L
                    Act = AuthoredCaseAct.Open Inputs.registration
                }
                None
            |> Result.defaultWith (fun _ -> failtest "OPEN positive control")

        let original = openCase first
        let snapshot = Case.snapshot original
        Expect.equal (CaseId.value snapshot.CaseId) first "Independent expected identity"

        Expect.notEqual
            snapshot.CaseId
            (Case.snapshot (openCase second)).CaseId
            "Distinct creations have distinct identities"

        let changed =
            CaseFixtures.decide
                (DateOnly(2026, 9, 7))
                {
                    OperationId = second
                    CaseReference = "immutable-reference"
                    ExpectedRevision = 1L
                    Act = AuthoredCaseAct.Bare BareAct.Close
                }
                (Some original)
            |> Result.defaultWith (fun _ -> failtest "Close control")
            |> Case.snapshot

        Expect.equal changed.CaseId snapshot.CaseId "Changes retain identity"

        Expect.equal
            (CaseReference.format changed.Reference)
            "Immutable-Reference"
            "Reference spelling is immutable")
