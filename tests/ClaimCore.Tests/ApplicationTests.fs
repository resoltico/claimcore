module ClaimCore.Tests.ApplicationTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.Tests.ApplicationFixtures

let private unavailable = ExecuteOutcome.Rejected Rejection.ResourceUnavailable

let private revokeCaseWork actor =
    task {
        for n, role in [ 300, Role.CaseHandler; 301, Role.PaymentRecorder ] do
            let! result = grant actor (id n) owner role GrantTarget.Installation false
            acceptedAuthority result
    }

let private replayAndSpelling (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! first = actor.Execute(original, CancellationToken.None)
        let receipt = accepted first
        let! replay = actor.Execute(original, CancellationToken.None)
        Expect.equal replay (ExecuteOutcome.AlreadyAccepted receipt) "Original receipt"

        let changed =
            { original with
                Act =
                    CaseAct.Business(
                        BusinessCommand.Open
                            { registration with
                                ClaimedAmount = "1000"
                            }
                    )
            }

        let! conflict = actor.Execute(changed, CancellationToken.None)

        Expect.equal
            conflict
            (ExecuteOutcome.Rejected Rejection.IdempotencyConflict)
            "Authored amount spelling is identity"

        let recased =
            { original with
                CaseReference = "unit-001"
            }

        let! caseConflict = actor.Execute(recased, CancellationToken.None)

        Expect.equal
            caseConflict
            (ExecuteOutcome.Rejected Rejection.IdempotencyConflict)
            "Resource folding does not rewrite canonical content"

        Expect.equal store.Entries.Length 4 "Replay and refusal append nothing"
    }

let private malformed (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let actor = core fixture owner

        let! invalid =
            actor.Execute(
                { openRequest 10 "UNIT-001" with
                    OperationId = Guid.Empty
                },
                CancellationToken.None
            )

        match invalid with
        | ExecuteOutcome.Rejected(Rejection.Case _) -> ()
        | _ -> failtest "Malformed request admitted"

        let bad =
            actRequest
                11
                "UNIT-001"
                1L
                (CaseAct.Stewardship(StewardshipAct.Void Unchecked.defaultof<string>))

        let! result = actor.Execute(bad, CancellationToken.None)

        Expect.equal
            result
            (ExecuteOutcome.Rejected(
                Rejection.Case(
                    CaseRefusal.InvalidInput(
                        InputTarget.Reason,
                        InputViolation.Text TextViolation.NonBlankRequired
                    )
                )
            ))
            "Reason format refuses before encoding"

        Expect.equal store.DecisionCalls 0 "No storage callback"
    }

let private definiteFailure (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        store.NextFailure <- StoreFailure.Unavailable
        let! failure = actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)
        Expect.equal failure (ExecuteOutcome.Failed CoreFault.StoreUnavailable) "Definite failure"
        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()
        let! result = actor.Execute(openRequest 10 "UNIT-001", cancelled.Token)
        Expect.equal result ExecuteOutcome.Cancelled "Cancellation before dispatch"
        Expect.equal store.Entries.Length 3 "Neither operation wrote"
    }

let private unknownCommit (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        for n, lands in [ 10, true; 11, false ] do
            store.UnknownCommitLands <- lands
            store.NextFailure <- StoreFailure.CommitOutcomeUnknown
            let original = openRequest n (if lands then "LANDED" else "NOT-LANDED")
            let! uncertain = actor.Execute(original, CancellationToken.None)

            match uncertain with
            | ExecuteOutcome.OutcomeUnknown(operationId, hash) ->
                Expect.equal operationId original.OperationId "Original identity"

                Expect.equal
                    hash
                    (RequestRecord.encode original
                     |> LedgerEntry.requestDigest
                     |> Convert.ToHexStringLower)
                    "Original canonical content"
            | _ -> failtest "Lost confirmation relabeled"

            let! result = actor.Execute(original, CancellationToken.None)

            match result, lands with
            | ExecuteOutcome.AlreadyAccepted _, true
            | ExecuteOutcome.Accepted _, false -> ()
            | _ -> failtest "Exact retry did not reconcile"

            Expect.equal
                (store.Entries
                 |> List.filter (fun entry -> entry.OperationId = original.OperationId)
                 |> List.length)
                1
                "One acceptance"
    }

let private acceptedAfterCancellation (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        use cancellation = new CancellationTokenSource()
        store.AfterCommit <- cancellation.Cancel
        let! result = actor.Execute(openRequest 10 "UNIT-001", cancellation.Token)
        accepted result |> ignore
        Expect.isTrue cancellation.IsCancellationRequested "Cancellation reached the boundary"
    }

let private previewAdvice (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! preview = actor.Preview(original, CancellationToken.None)

        match preview with
        | PreviewOutcome.Reviewed(review, _) -> Expect.isNone review.Before "Advisory initial state"
        | _ -> failtest "Preview failed"

        Expect.equal store.Entries.Length 3 "Preview stores nothing"
        let! other = actor.Execute(openRequest 11 "UNIT-001", CancellationToken.None)
        accepted other |> ignore
        let! result = actor.Execute(original, CancellationToken.None)

        Expect.equal
            result
            (ExecuteOutcome.Rejected(Rejection.Case CaseRefusal.AlreadyExists))
            "Execution rechecks current record"
    }

let private businessDate (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        store.Instant <- DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)
        let! result = actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)

        Expect.equal
            result
            (ExecuteOutcome.Rejected(
                Rejection.Case(
                    CaseRefusal.InvalidInput(
                        InputTarget.IncidentNotificationDate,
                        InputViolation.Date DateViolation.FutureDate
                    )
                )
            ))
            "Captured database instant supplies the business date"

        Expect.equal store.Entries.Length 3 "No future assertion"
    }

let private revokedRights (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! result = actor.Execute(original, CancellationToken.None)
        accepted result |> ignore
        do! revokeCaseWork actor
        let! replay = actor.Execute(original, CancellationToken.None)
        Expect.equal replay unavailable "Current authority precedes replay"
        let! current = actor.Get("UNIT-001", CancellationToken.None)

        Expect.equal
            current
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Facades cache no authority"
    }

let private unavailableResources (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        let! granted =
            grant actor (id 3) owner Role.DataSteward GrantTarget.Installation true

        acceptedAuthority granted

        for n, reference in [ 10, "LIVE"; 11, "ERASED" ] do
            let! opened = actor.Execute(openRequest n reference, CancellationToken.None)
            accepted opened |> ignore

        let! erased =
            actor.Execute(
                actRequest 12 "ERASED" 1L (CaseAct.Stewardship StewardshipAct.Erase),
                CancellationToken.None
            )

        accepted erased |> ignore
        do! revokeCaseWork actor

        let! revoked =
            grant actor (id 4) owner Role.DataSteward GrantTarget.Installation false

        acceptedAuthority revoked
        let! absent = actor.Get("ABSENT", CancellationToken.None)
        let! inaccessible = actor.Get("LIVE", CancellationToken.None)
        let! erased = actor.Get("ERASED", CancellationToken.None)
        Expect.equal absent inaccessible "Absent and inaccessible agree"
        Expect.equal erased absent "Erased beyond view agrees"
    }

let private lifecycle (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        let! granted =
            grant actor (id 3) owner Role.DataSteward GrantTarget.Installation true

        acceptedAuthority granted
        let! opened = actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)
        accepted opened |> ignore

        let! voided =
            actor.Execute(
                actRequest
                    11
                    "UNIT-001"
                    1L
                    (CaseAct.Stewardship(StewardshipAct.Void "Wrong reference")),
                CancellationToken.None
            )

        Expect.equal (accepted voided |> caseReceipt).Revision 2L "One revision"

        let! blocked =
            actor.Execute(request 12 "UNIT-001" 2L BusinessCommand.Close, CancellationToken.None)

        Expect.equal
            blocked
            (ExecuteOutcome.Rejected(Rejection.Case CaseRefusal.CaseVoided))
            "Domain blocks business work"

        let! restored =
            actor.Execute(
                actRequest
                    13
                    "UNIT-001"
                    2L
                    (CaseAct.Stewardship(StewardshipAct.Reinstate "Reference confirmed")),
                CancellationToken.None
            )

        Expect.equal (accepted restored |> caseReceipt).Revision 3L "Reinstatement revision"

        let! closed =
            actor.Execute(request 14 "UNIT-001" 3L BusinessCommand.Close, CancellationToken.None)

        accepted closed |> ignore
    }

let private scopedCreation (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        let! opened = admin.Execute(openRequest 10 "VISIBLE", CancellationToken.None)
        accepted opened |> ignore

        let scoped =
            PrincipalKey.human "https://issuer.example/" "scoped"
            |> Result.defaultWith failtest

        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 3) scoped AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 4) scoped Role.CaseHandler (GrantTarget.CaseReference "visible") true

        acceptedAuthority granted
        let actor = core fixture scoped
        let! refused = actor.Execute(openRequest 11 "OTHER", CancellationToken.None)
        Expect.equal refused unavailable "Creation requires installation registration authority"
        let! page = actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)

        match page with
        | QueryOutcome.Succeeded page -> Expect.equal page.Items.Length 1 "Visible case"
        | _ -> failtest "Missing scoped page"

        let! context = actor.Context CancellationToken.None

        match context with
        | QueryOutcome.Succeeded value ->
            Expect.isFalse
                (List.contains Capability.Register value.Capabilities)
                "No installation OPEN advice"
        | _ -> failtest "Missing context"

        let! revoked =
            grant admin (id 5) scoped Role.CaseHandler (GrantTarget.CaseReference "VISIBLE") false

        acceptedAuthority revoked
        let! denied = actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)

        Expect.equal
            denied
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Visibility is rechecked per page"
    }

let private corruptIdentity (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! opened = actor.Execute(original, CancellationToken.None)
        accepted opened |> ignore

        let decide (view: CaseLockedView) =
            let stored = Option.get view.ExistingOperation

            let payload =
                stored.Payload
                |> Option.map (fun value ->
                    let changed = Array.copy value
                    changed[changed.Length - 1] <- changed[changed.Length - 1] ^^^ 1uy
                    changed)

            let invalid = { stored with Payload = payload }

            match
                OperationIdentity.decide
                    installation
                    owner
                    view.Actor
                    original.OperationId
                    (RequestRecord.encode original)
                    (Some invalid)
            with
            | IdentityDecision.Fail CoreFault.OperationContentConflict ->
                LedgerDecision.Refuse Rejection.ResourceUnavailable
            | _ -> failtest "Corrupted payload disclosed an identity or receipt"

        let! result =
            fixture.Store.WriteCase(
                owner,
                original.OperationId,
                original.CaseReference,
                decide,
                CancellationToken.None
            )

        Expect.equal
            result
            (Ok(LedgerWriteResult.Refused Rejection.ResourceUnavailable))
            "Existing commitment detects changed payload"
    }

let private privateIdentity (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture

        for n, reference in [ 10, "VISIBLE"; 11, "PRIVATE" ] do
            let! result = admin.Execute(openRequest n reference, CancellationToken.None)
            accepted result |> ignore

        let target =
            PrincipalKey.human "https://issuer.example/" "scoped"
            |> Result.defaultWith failtest

        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 3) target AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 4) target Role.CaseHandler (GrantTarget.CaseReference "VISIBLE") true

        acceptedAuthority granted

        let! refused =
            (core fixture target)
                .Execute(request 11 "VISIBLE" 1L BusinessCommand.Close, CancellationToken.None)

        Expect.equal refused unavailable "No conflict oracle for an inaccessible operation"
    }

let private failedProjection (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let! opened = actor.Execute(openRequest 10 "UNIT-001", CancellationToken.None)
        let placeholder = accepted opened
        let before = store.Entries.Length

        let decide (view: CaseLockedView) =
            let draft =
                LedgerEntries.create
                    installation
                    view.Head
                    view.ObservedAt
                    view.PayloadNonce
                    (id 1)
                    (id 99)
                    (StoredScope.Case(id 99, 1L, CaseActKind.Erase, DateOnly(2026, 9, 7)))
                    [| 1uy |]
                    Array.empty

            LedgerDecision.Append(draft, CaseProjection.Erase(id 99, 1L), placeholder)

        let! result =
            fixture.Store.WriteCase(owner, id 99, "MISSING", decide, CancellationToken.None)

        Expect.equal
            result
            (Error StoreFailure.Integrity)
            "Projection failure rolls back the append"

        Expect.equal store.Entries.Length before "No orphan entry"
        Expect.equal store.Cases.Length 1 "No projection change"
    }

let private interruptedCommit (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        store.AfterCommit <- fun () -> raise (OperationCanceledException())
        let original = openRequest 10 "UNIT-001"
        let! result = actor.Execute(original, CancellationToken.None)

        match result with
        | ExecuteOutcome.OutcomeUnknown _ -> ()
        | _ -> failtest "Lost confirmation relabeled"

        store.AfterCommit <- ignore
        let! retry = actor.Execute(original, CancellationToken.None)

        match retry with
        | ExecuteOutcome.AlreadyAccepted _ -> ()
        | _ -> failtest "Committed identity did not replay"
    }

let private replayAfterRevision (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! first = actor.Execute(original, CancellationToken.None)
        let receipt = accepted first

        let! closed =
            actor.Execute(request 11 "unit-001" 1L BusinessCommand.Close, CancellationToken.None)

        Expect.equal
            (accepted closed |> caseReceipt).Reference
            "UNIT-001"
            "Initial authored reference is retained"

        let! replay = actor.Execute(original, CancellationToken.None)

        Expect.equal
            replay
            (ExecuteOutcome.AlreadyAccepted receipt)
            "Full-history actor sees the original snapshot"

        let! preview = actor.Preview(original, CancellationToken.None)

        Expect.equal
            preview
            (PreviewOutcome.AlreadyAccepted receipt)
            "No fresh proposal for accepted identity"
    }

let private headExhaustion (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! opened = actor.Execute(original, CancellationToken.None)
        let receipt = accepted opened
        let before = store.Entries.Length

        store.HeadOverride <-
            Some
                {
                    Sequence = Revision.Maximum
                    EntryHash = Convert.FromHexString receipt.Position.EntryHash
                }

        let! fresh =
            actor.Execute(request 11 "UNIT-001" 1L BusinessCommand.Close, CancellationToken.None)

        Expect.equal
            fresh
            (ExecuteOutcome.Rejected Rejection.LedgerExhausted)
            "No global sequence arithmetic at M"

        let! replay = actor.Execute(original, CancellationToken.None)

        Expect.equal
            replay
            (ExecuteOutcome.AlreadyAccepted receipt)
            "Authorized exact replay still works"

        let target =
            PrincipalKey.human "https://issuer.example/" "another"
            |> Result.defaultWith failtest

        let! authority =
            actor.ExecuteAuthority(
                authorityRequest (id 12) target AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        Expect.equal
            authority
            (ExecuteOutcome.Rejected Rejection.LedgerExhausted)
            "Authority append shares the bound"

        Expect.equal store.Entries.Length before "No accepted state changed"
    }

[<Tests>]
let tests =
    testList
        "Application case decisions"
        [
            testCaseAsync
                "[CC-APP-002] replay is stable and authored amount and reference spelling conflict"
                (run replayAndSpelling)
            testCaseAsync
                "[CC-APP-001] malformed business and stewardship input performs no store work"
                (run malformed)
            testCaseAsync
                "[CC-APP-001] failure and pre-dispatch cancellation record nothing"
                (run definiteFailure)
            testCaseAsync
                "[CC-APP-002] commit uncertainty resolves by exact retry whether it landed or not"
                (run unknownCommit)
            testCaseAsync
                "acceptance is not relabeled by cancellation after commit"
                (run acceptedAfterCancellation)
            testCaseAsync
                "stateless preview remains advice and execution rechecks current state"
                (run previewAdvice)
            testCaseAsync
                "business dates use the supplied snapshot instant and calendar"
                (run businessDate)
            testCaseAsync
                "[CC-AUTH-001] revoked authority denies replay and current case reads"
                (run revokedRights)
            testCaseAsync
                "[CC-AUTH-001] absent inaccessible and erased-beyond-view resources refuse identically"
                (run unavailableResources)
            testCaseAsync
                "[CC-LIFE-001] Domain stewardship advances once blocks work and reinstates"
                (run lifecycle)
            testCaseAsync
                "case-scoped handling cannot create and listing rechecks visibility"
                (run scopedCreation)
            testCaseAsync
                "stored identity corruption is a fault and discloses no receipt"
                (run corruptIdentity)
            testCaseAsync
                "private operation identity does not leak through a readable different case"
                (run privateIdentity)
            testCaseAsync
                "failed projection publishes neither entry nor projection"
                (run failedProjection)
            testCaseAsync
                "post-commit interruption remains unknown and exact retry is accepted"
                (run interruptedCommit)
            testCaseAsync
                "exact replay precedes later revisions and preview observes acceptance"
                (run replayAfterRevision)
            testCaseAsync
                "global head exhaustion refuses fresh case and authority acts while permitting replay"
                (run headExhaustion)
            ClaimCore.TestSupport.StoreContractTests.tests (fun () ->
                let _, value = fixture () in Task.FromResult value)
        ]
