module ClaimCore.Tests.ApplicationTests

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.Web.Composition
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.Tests.ApplicationFixtures

let private unavailable = ExecuteResult.Rejected Refusal.ResourceUnavailable

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
        let! first = actor.Execute(OperationInput.Case original, CancellationToken.None)
        let receipt = accepted first
        let! replay = actor.Execute(OperationInput.Case original, CancellationToken.None)
        Expect.equal replay.Outcome (ExecuteResult.AlreadyAccepted receipt) "Original receipt"

        let changed =
            { original with
                Act =
                    (CaseAct.Open
                        { registration with
                            ClaimedAmount = "1000"
                        })
            }

        let! conflict = actor.Execute(OperationInput.Case changed, CancellationToken.None)

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Authored amount spelling is identity"

        let recased =
            { original with
                CaseReference = "unit-001"
            }

        let! caseConflict =
            actor.Execute(OperationInput.Case recased, CancellationToken.None)

        Expect.equal
            caseConflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Resource folding does not rewrite canonical content"

        Expect.equal store.Entries.Length 4 "Replay and refusal append nothing"
    }

let private rawOpening =
    """{"scope":"CASE","operationId":"80000000-0000-4000-8000-000000000010","caseReference":"UNIT-001","expectedRevision":0,"act":{"kind":"OPEN","values":{"incidentDate":"2026-08-01","incidentNotificationDate":"2026-08-03","incidentCountry":"LV","claimantName":"Synthetic Party","insurerName":"Synthetic Insurer","claimedAmount":"1000.00","claimedCurrency":"EUR"}}}"""

let private respelledOpening =
    """{
      "scope": "CASE",
      "act": {
        "values": {
          "claimedCurrency": "EUR", "claimedAmount": "1000.00",
          "insurerName": "Synthetic Insurer", "clai\u006dantName": "Synthetic\u0020Party",
          "incidentCountry": "L\u0056", "incidentNotificationDate": "2026-08-03",
          "incidentDate": "2026-08-01"
        },
        "kind": "OPEN"
      },
      "expectedRevision": 0, "caseReference": "\u0055NIT-001",
      "operationId": "80000000-0000-4000-8000-000000000010"
    }"""

let private decodeWire (text: string) =
    Codec.decode
        Limits.MaximumRequestBytes
        Api.operationExecute.Input
        (Text.Encoding.UTF8.GetBytes text)
    |> Result.map (function
        | OperationInput.Case value -> value
        | _ -> failtest "Expected a case execution request")
    |> Result.defaultWith (fun _ -> failtest "Synthetic raw request must decode")

let private rawWireReplay (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let before = store.Entries.Length

        let! first =
            actor.Execute(OperationInput.Case(decodeWire rawOpening), CancellationToken.None)

        let receipt = accepted first
        Expect.equal receipt.RecordedBy (id 1) "Original executor attribution"
        Expect.equal receipt.RecordedAt instant "Original database instant"
        Expect.notEqual rawOpening respelledOpening "Different raw JSON spellings"
        store.Instant <- instant.AddMinutes 1.0

        let! replay =
            actor.Execute(OperationInput.Case(decodeWire respelledOpening), CancellationToken.None)

        Expect.equal
            replay.Outcome
            (ExecuteResult.AlreadyAccepted receipt)
            "Original entire receipt"

        let changed =
            respelledOpening.Replace("1000.00", "1000.0", StringComparison.Ordinal)

        let! conflict =
            actor.Execute(OperationInput.Case(decodeWire changed), CancellationToken.None)

        Expect.equal
            conflict.Outcome
            (ExecuteResult.Rejected Refusal.IdempotencyConflict)
            "Authored amount text is content despite equal decimal value"

        Expect.equal store.Entries.Length (before + 1) "Only original acceptance appended"
    }

let private malformed (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let actor = core fixture owner

        let! invalid =
            actor.Execute(
                OperationInput.Case(
                    { openRequest 10 "UNIT-001" with
                        OperationId = Guid.Empty
                    }
                ),
                CancellationToken.None
            )

        match invalid.Outcome with
        | ExecuteResult.Rejected(_) -> ()
        | _ -> failtest "Malformed request admitted"

        let bad = actRequest 11 "UNIT-001" 1L ((CaseAct.Void Unchecked.defaultof<string>))

        let! result = actor.Execute(OperationInput.Case bad, CancellationToken.None)

        Expect.equal
            result.Outcome
            (ExecuteResult.Rejected(
                (Refusal.InvalidInput(InputTarget.Reason, Violation.TextRequired))
            ))
            "Reason format refuses before encoding"

        Expect.equal store.DecisionCalls 0 "No storage callback"
    }

let private definiteFailure (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        store.NextFailure <- StoreFailure.Unavailable

        let! failure =
            actor.Execute(OperationInput.Case(openRequest 10 "UNIT-001"), CancellationToken.None)

        Expect.equal
            failure.Outcome
            (ExecuteResult.Failed CoreFault.StoreUnavailable)
            "Definite failure"

        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        let! result =
            actor.Execute(OperationInput.Case(openRequest 10 "UNIT-001"), cancelled.Token)

        Expect.equal
            result.Outcome
            (ExecuteResult.Failed CoreFault.StoreUnavailable)
            "Cancellation before dispatch"

        Expect.equal result.OperationId (id 10) "Cancelled execution keeps original identity"

        let! preview =
            actor.Preview(OperationInput.Case(openRequest 10 "UNIT-001"), cancelled.Token)

        Expect.equal preview.OperationId (id 10) "Cancelled preview keeps original identity"

        Expect.equal
            preview.Outcome
            (PreviewResult.Failed CoreFault.StoreUnavailable)
            "Cancelled preview is a definite failure"

        let! context = actor.Context cancelled.Token

        Expect.equal
            context
            (ReadReply.Failed CoreFault.StoreUnavailable)
            "A cancelled read maps to the public store fault."

        let! current = actor.Get({ CaseReference = "UNIT-001" }, cancelled.Token)

        Expect.equal
            current
            (ReadReply.Failed CoreFault.StoreUnavailable)
            "Cancelled case reads disclose no snapshot."

        let! observed = actor.Observe({ OperationId = id 10 }, cancelled.Token)

        Expect.equal
            observed
            (ReadReply.Failed CoreFault.StoreUnavailable)
            "Cancelled observations disclose no receipt."

        Expect.equal store.Entries.Length 3 "Neither operation wrote"
    }

let private unknownCommit (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        for n, lands in [ 10, true; 11, false ] do
            store.UnknownCommitLands <- lands
            store.NextFailure <- StoreFailure.CommitOutcomeUnknown
            let original = openRequest n (if lands then "LANDED" else "NOT-LANDED")
            let! uncertain = actor.Execute(OperationInput.Case original, CancellationToken.None)

            match uncertain.Outcome with
            | ExecuteResult.OutcomeUnknown ->
                Expect.equal uncertain.OperationId original.OperationId "Original identity"

            | _ -> failtest "Lost confirmation relabeled"

            let! result = actor.Execute(OperationInput.Case original, CancellationToken.None)

            match result.Outcome, lands with
            | ExecuteResult.AlreadyAccepted _, true
            | ExecuteResult.Accepted _, false -> ()
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

        let! result =
            actor.Execute(OperationInput.Case(openRequest 10 "UNIT-001"), cancellation.Token)

        accepted result |> ignore
        Expect.isTrue cancellation.IsCancellationRequested "Cancellation reached the boundary"
    }

let private previewAdvice (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! preview = actor.Preview(OperationInput.Case original, CancellationToken.None)

        match preview.Outcome with
        | PreviewResult.Reviewed(OperationReview.Case review) ->
            Expect.isNone review.Before "Advisory initial state"
        | _ -> failtest "Preview failed"

        Expect.equal store.Entries.Length 3 "Preview stores nothing"

        let! other =
            actor.Execute(OperationInput.Case(openRequest 11 "UNIT-001"), CancellationToken.None)

        accepted other |> ignore
        let! result = actor.Execute(OperationInput.Case original, CancellationToken.None)

        Expect.equal
            result.Outcome
            (ExecuteResult.Rejected(Refusal.CaseAlreadyExists))
            "Execution rechecks current record"
    }

let private businessDate (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        store.Instant <- DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero)

        let! result =
            actor.Execute(OperationInput.Case(openRequest 10 "UNIT-001"), CancellationToken.None)

        Expect.equal
            result.Outcome
            (ExecuteResult.Rejected(
                (Refusal.InvalidInput(
                    (InputTarget.Field BusinessField.IncidentNotificationDate),
                    Violation.FutureDate
                ))
            ))
            "Captured database instant supplies the business date"

        Expect.equal store.Entries.Length 3 "No future assertion"
    }

let private revokedRights (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! result = actor.Execute(OperationInput.Case original, CancellationToken.None)
        accepted result |> ignore
        do! revokeCaseWork actor
        let! replay = actor.Execute(OperationInput.Case original, CancellationToken.None)
        Expect.equal replay.Outcome unavailable "Current authority precedes replay"
        let! current = actor.Get({ CaseReference = "UNIT-001" }, CancellationToken.None)

        Expect.equal
            current
            (ReadReply.Rejected Refusal.ResourceUnavailable)
            "Facades cache no authority"
    }

let private unavailableResources (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        let! granted =
            grant actor (id 3) owner Role.DataSteward GrantTarget.Installation true

        acceptedAuthority granted

        for n, reference in [ 10, "LIVE"; 11, "ERASED" ] do
            let! opened =
                actor.Execute(OperationInput.Case(openRequest n reference), CancellationToken.None)

            accepted opened |> ignore

        let! erased =
            actor.Execute(
                OperationInput.Case(actRequest 12 "ERASED" 1L (CaseAct.Erase)),
                CancellationToken.None
            )

        accepted erased |> ignore
        do! revokeCaseWork actor

        let! revoked =
            grant actor (id 4) owner Role.DataSteward GrantTarget.Installation false

        acceptedAuthority revoked
        let! absent = actor.Get({ CaseReference = "ABSENT" }, CancellationToken.None)
        let! inaccessible = actor.Get({ CaseReference = "LIVE" }, CancellationToken.None)
        let! erased = actor.Get({ CaseReference = "ERASED" }, CancellationToken.None)
        Expect.equal absent inaccessible "Absent and inaccessible agree"
        Expect.equal erased absent "Erased beyond view agrees"
    }

let private lifecycle (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        let! granted =
            grant actor (id 3) owner Role.DataSteward GrantTarget.Installation true

        acceptedAuthority granted

        let! opened =
            actor.Execute(OperationInput.Case(openRequest 10 "UNIT-001"), CancellationToken.None)

        accepted opened |> ignore

        let! voided =
            actor.Execute(
                OperationInput.Case(actRequest 11 "UNIT-001" 1L ((CaseAct.Void "Wrong reference"))),
                CancellationToken.None
            )

        Expect.equal (accepted voided |> caseReceipt).Revision 2L "One revision"

        let! blocked =
            actor.Execute(
                OperationInput.Case(request 12 "UNIT-001" 2L CaseAct.Close),
                CancellationToken.None
            )

        Expect.equal
            blocked.Outcome
            (ExecuteResult.Rejected(Refusal.CaseVoided))
            "Domain blocks business work"

        let! restored =
            actor.Execute(
                OperationInput.Case(
                    actRequest 13 "UNIT-001" 2L ((CaseAct.Reinstate "Reference confirmed"))
                ),
                CancellationToken.None
            )

        Expect.equal (accepted restored |> caseReceipt).Revision 3L "Reinstatement revision"

        let! closed =
            actor.Execute(
                OperationInput.Case(request 14 "UNIT-001" 3L CaseAct.Close),
                CancellationToken.None
            )

        accepted closed |> ignore
    }

let private scopedCreation (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture

        let! opened =
            admin.Execute(OperationInput.Case(openRequest 10 "VISIBLE"), CancellationToken.None)

        accepted opened |> ignore

        let scoped =
            PrincipalKey.human "https://issuer.example/" "scoped"
            |> Result.defaultWith failtest

        let! registered =
            admin.Execute(
                OperationInput.Authority(
                    authorityRequest (id 3) scoped AdmittedAuthorityAct.RegisterActor
                ),
                CancellationToken.None
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 4) scoped Role.CaseHandler (GrantTarget.CaseReference "visible") true

        acceptedAuthority granted
        let actor = core fixture scoped

        let! refused =
            actor.Execute(OperationInput.Case(openRequest 11 "OTHER"), CancellationToken.None)

        Expect.equal
            refused.Outcome
            unavailable
            "Creation requires installation registration authority"

        let! page = actor.List({ AfterCursor = None; Limit = 1 }, CancellationToken.None)

        match page with
        | ReadReply.Succeeded page -> Expect.equal page.Items.Length 1 "Visible case"
        | _ -> failtest "Missing scoped page"

        let! context = actor.Context CancellationToken.None

        match context with
        | ReadReply.Succeeded value ->
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
            (ReadReply.Rejected Refusal.ResourceUnavailable)
            "Visibility is rechecked per page"
    }

let private corruptIdentity (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! opened = actor.Execute(OperationInput.Case original, CancellationToken.None)
        accepted opened |> ignore

        let decide (view: CaseLockedView) =
            let stored = Option.get view.Read.ExistingOperation

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
                    view.Read.Actor
                    original.OperationId
                    (RequestRecord.encode original)
                    (Some invalid)
            with
            | IdentityDecision.Fail CoreFault.OperationContentConflict ->
                LedgerDecision.Refuse Refusal.ResourceUnavailable
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
            (Ok(LedgerWriteResult.Refused Refusal.ResourceUnavailable))
            "Existing commitment detects changed payload"
    }

let private privateIdentity (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture

        for n, reference in [ 10, "VISIBLE"; 11, "PRIVATE" ] do
            let! result =
                admin.Execute(OperationInput.Case(openRequest n reference), CancellationToken.None)

            accepted result |> ignore

        let target =
            PrincipalKey.human "https://issuer.example/" "scoped"
            |> Result.defaultWith failtest

        let! registered =
            admin.Execute(
                OperationInput.Authority(
                    authorityRequest (id 3) target AdmittedAuthorityAct.RegisterActor
                ),
                CancellationToken.None
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 4) target Role.CaseHandler (GrantTarget.CaseReference "VISIBLE") true

        acceptedAuthority granted

        let! refused =
            (core fixture target)
                .Execute(
                    OperationInput.Case(request 11 "VISIBLE" 1L CaseAct.Close),
                    CancellationToken.None
                )

        Expect.equal refused.Outcome unavailable "No conflict oracle for an inaccessible operation"
    }

let private failedProjection (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture

        let! opened =
            actor.Execute(OperationInput.Case(openRequest 10 "UNIT-001"), CancellationToken.None)

        let placeholder = accepted opened
        let before = store.Entries.Length

        let decide (view: CaseLockedView) =
            let draft =
                LedgerEntries.create
                    installation
                    view.Head
                    view.Read.ObservedAt
                    view.PayloadNonce
                    (id 1)
                    (id 99)
                    (StoredScope.Case(
                        id 99,
                        1L,
                        (CaseActKind.Stewardship StewardshipActKind.Erase),
                        DateOnly(2026, 9, 7)
                    ))
                    [| 1uy |]
                    Array.empty

            LedgerDecision.Append(draft, CaseProjection.Erase(id 99, 1L), placeholder)

        let! result =
            fixture.Store.WriteCase(owner, id 99, "MISSING", decide, CancellationToken.None)

        Expect.equal result (Error StoreFailure.Internal) "Projection failure rolls back the append"

        Expect.equal store.Entries.Length before "No orphan entry"
        Expect.equal store.Cases.Length 1 "No projection change"
    }

let private interruptedCommit (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        store.AfterCommit <- fun () -> raise (OperationCanceledException())
        let original = openRequest 10 "UNIT-001"
        let! result = actor.Execute(OperationInput.Case original, CancellationToken.None)

        match result.Outcome with
        | ExecuteResult.OutcomeUnknown -> ()
        | _ -> failtest "Lost confirmation relabeled"

        store.AfterCommit <- ignore
        let! retry = actor.Execute(OperationInput.Case original, CancellationToken.None)

        match retry.Outcome with
        | ExecuteResult.AlreadyAccepted _ -> ()
        | _ -> failtest "Committed identity did not replay"
    }

let private replayAfterRevision (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! first = actor.Execute(OperationInput.Case original, CancellationToken.None)
        let receipt = accepted first

        let! closed =
            actor.Execute(
                OperationInput.Case(request 11 "unit-001" 1L CaseAct.Close),
                CancellationToken.None
            )

        Expect.equal
            (accepted closed |> caseReceipt).Reference
            "UNIT-001"
            "Initial authored reference is retained"

        let! replay = actor.Execute(OperationInput.Case original, CancellationToken.None)

        Expect.equal
            replay.Outcome
            (ExecuteResult.AlreadyAccepted receipt)
            "Full-history actor sees the original snapshot"

        let! preview = actor.Preview(OperationInput.Case original, CancellationToken.None)

        Expect.equal
            preview.Outcome
            (PreviewResult.AlreadyAccepted receipt)
            "No fresh proposal for accepted identity"
    }

let private headExhaustion (store: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! actor = caseWorker fixture
        let original = openRequest 10 "UNIT-001"
        let! opened = actor.Execute(OperationInput.Case original, CancellationToken.None)
        let receipt = accepted opened
        let before = store.Entries.Length

        store.HeadOverride <-
            Some
                {
                    Sequence = Revision.Maximum
                    EntryHash = Convert.FromHexString receipt.Position.EntryHash
                }

        let! fresh =
            actor.Execute(
                OperationInput.Case(request 11 "UNIT-001" 1L CaseAct.Close),
                CancellationToken.None
            )

        Expect.equal
            fresh.Outcome
            (ExecuteResult.Rejected Refusal.LedgerExhausted)
            "No global sequence arithmetic at M"

        let! replay = actor.Execute(OperationInput.Case original, CancellationToken.None)

        Expect.equal
            replay.Outcome
            (ExecuteResult.AlreadyAccepted receipt)
            "Authorized exact replay still works"

        let target =
            PrincipalKey.human "https://issuer.example/" "another"
            |> Result.defaultWith failtest

        let! authority =
            actor.Execute(
                OperationInput.Authority(
                    authorityRequest (id 12) target AdmittedAuthorityAct.RegisterActor
                ),
                CancellationToken.None
            )

        Expect.equal
            authority.Outcome
            (ExecuteResult.Rejected Refusal.LedgerExhausted)
            "Authority append shares the bound"

        Expect.equal store.Entries.Length before "No accepted state changed"
    }

[<Tests>]
let tests =
    testList
        "Application case decisions"
        [
            testCaseAsync
                "raw wire re-spelling replays the original accepted receipt and attribution"
                (run rawWireReplay)
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
