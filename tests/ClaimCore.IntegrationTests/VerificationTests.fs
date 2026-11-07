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


let private requestEffectAgreement (database: PostgresFixture.Database) =
    task {
        let! _ = populated database
        let sequence = CaseFixtures.sequence 5L
        let! original = row database sequence
        let request, snapshot = livePayload original

        let forged =
            { snapshot with
                State =
                    CaseState.map
                        (fun fields ->
                            { fields with
                                Status = CaseStatus.Closed
                            })
                        snapshot.State
            }

        Expect.isOk (Case.ofAccepted forged) "CLOSED remains intrinsically representable"
        // Recommit the retained request/result, rehash every affected link and replace the head.
        do! rewrite database sequence (updateCase request forged)
        let bytes = CaseRecord.encodeSnapshot forged
        // Only status and snapshot change; all other projection facts remain the same.
        do!
            sql
                database
                "UPDATE claimcore.cases SET snapshot=$1,status='CLOSED' WHERE case_id=$2"
                [| box bytes; box (CaseId.value forged.CaseId) |]

        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! rows =
            DatabaseSession.rows
                connection
                "SELECT snapshot,status FROM claimcore.cases WHERE case_id=$1"
                [| box (CaseId.value forged.CaseId) |]
                token

        Expect.equal
            (DatabaseSession.value<byte array> 0 rows.Head)
            bytes
            "Projection agrees with the forged retained snapshot"

        Expect.equal
            (DatabaseSession.value<string> 1 rows.Head)
            "CLOSED"
            "Independent closed projection expectation"

        let! retained = row database sequence
        let _, retainedSnapshot = livePayload retained

        Expect.equal
            retainedSnapshot
            forged
            "The rehashed retained snapshot matches the materialized state"

        let! result = Verification.verify database.OwnerSource None token

        Expect.equal
            result
            (VerificationResult.Diverged
                {
                    Sequence = Some sequence
                    Finding = VerificationFinding.StateSequence
                })
            "A self-consistent hash, head and projection cannot substitute for the OPEN request effect"
    }

let private workIndexPrecedence (database: PostgresFixture.Database) =
    task {
        let! actor = populated database
        let! resolved = Service.resolve actor { OperationId = id 997 } token

        Expect.equal
            resolved.Outcome
            (Outcome.Succeeded Resolved.NotAccepted)
            "The positive control has an independently retained recovery fence"

        let! checkpoint = verified database
        use! connection = database.OwnerSource.OpenConnectionAsync()

        let! fences =
            DatabaseSession.rows
                connection
                "SELECT actor_id,operation_id,fenced_at FROM claimcore.operation_fences"
                [||]
                token

        let fence = List.exactlyOne fences

        do!
            sql
                database
                "UPDATE claimcore.cases SET status='CLOSED',follow_up=DATE '2027-12-31',work_search_text='unrelated search material'; UPDATE claimcore.actors SET enabled=false; DELETE FROM claimcore.operation_fences"
                [||]

        do! finding VerificationFinding.CaseStatusProjection database
        do! sql database "UPDATE claimcore.cases SET status='OPENED'" [||]
        do! finding VerificationFinding.AuthorityProjection database
        do! sql database "UPDATE claimcore.actors SET enabled=true" [||]
        do! finding VerificationFinding.CaseWorkProjection database
        do! sql database "UPDATE claimcore.cases SET follow_up=NULL" [||]
        do! finding VerificationFinding.CaseWorkProjection database
        do! sql database "UPDATE claimcore.cases SET work_search_text=NULL" [||]
        do! finding VerificationFinding.FenceProjection database

        do!
            sql
                database
                "INSERT INTO claimcore.operation_fences(actor_id,operation_id,fenced_at) VALUES($1,$2,$3)"
                fence

        let! restored = verified database

        Expect.equal
            restored
            checkpoint
            "Repairing only projections restores the original verified head"
    }

[<Tests>]
let tests =
    testList
        "typed ledger verification"
        [
            testCaseAsync
                "a self-consistent rehashed OPEN cannot assert a CLOSED result"
                (run requestEffectAgreement)
            for occurrences in [ 2; 3 ] do
                for earlier in [ None; Some 5L; Some 6L ] do
                    testCaseAsync
                        ($"duplicate identity with {occurrences} occurrences and corruption {earlier}")
                        (run (duplicateIdentity earlier occurrences))
            testCase "all inventoried finding identifiers are explicitly covered" (fun () ->
                Expect.equal
                    (findingCases |> List.map (fst >> VerificationFinding.token) |> Set.ofList)
                    ((VerificationFinding.all |> List.map (fst >> VerificationFinding.token))
                     |> Set.ofList)
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
                "case work indexes preserve case authority work and fence finding precedence"
                (run workIndexPrecedence)
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
