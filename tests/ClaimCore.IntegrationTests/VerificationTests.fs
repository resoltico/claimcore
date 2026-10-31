module ClaimCore.IntegrationTests.VerificationTests

open ClaimCore.TestSupport

open ClaimCore.Protocol

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport
open ClaimCore.IntegrationTests.VerificationScenarioInputs
open ClaimCore.IntegrationTests.LedgerFindingScenarios
open ClaimCore.IntegrationTests.AuthorityFindingScenarios
open ClaimCore.IntegrationTests.StoredDataFindingScenarios


[<Tests>]
let tests =
    testList
        "typed ledger verification"
        [
            for occurrences in [ 2; 3 ] do
                for earlier in [ None; Some 4L; Some 5L ] do
                    testCaseAsync
                        ($"duplicate identity with {occurrences} occurrences and corruption {earlier}")
                        (run (duplicateIdentity earlier occurrences))
            testCase "all inventoried finding identifiers are explicitly covered" (fun () ->
                Expect.equal
                    (findingCases |> List.map (fst >> VerificationFinding.token) |> Set.ofList)
                    (Inventory.tokens "verificationFindings" |> Set.ofList)
                    "Closed independent finding inventory"

                Expect.equal
                    (findingCases |> List.map fst |> Set.ofList)
                    (VerificationFinding.all |> List.map fst |> Set.ofList)
                    "Every finding has an independent real-boundary case")
            for code, check in findingCases do
                testCaseAsync
                    (VerificationFinding.token code + " has an exact real-boundary control")
                    (run check)
            testCaseAsync "disabled historical executors are divergence" (run disabledExecutor)
            testCaseAsync
                "human-only grant targets remain a permanent authority invariant"
                (run serviceGrantTarget)
            testCaseAsync
                "authority history cannot remove the last enabled human installation owner"
                (run lastOwner)
            testCaseAsync
                "verification does not replay today's case capability matrix"
                (run permanentPolicyOnly)
            testCaseAsync
                "the first finding has stable precedence under multiple damaged properties"
                (run precedence)
            testCaseAsync
                "malformed actor projections are divergence rather than I/O failure"
                (run malformedProjection)
            testCaseAsync
                "case-scoped authority is resolved against earlier ledger cases, not final projections"
                (run priorCaseRequired)
            testCaseAsync
                "malformed retained instants are typed payload divergence"
                (run malformedInstant)
            testCaseAsync
                "an invalid optional projection date cannot disappear as NULL"
                (run malformedOptionalDate)
            testCaseAsync "malformed numeric projections are typed divergence" (run malformedAmount)
            testCaseAsync "real database I/O failure remains FAILED" (run ioFailure)
        ]
