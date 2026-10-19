module ClaimCore.Tests.DisclosureTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.TestSupport.StoreContractTests
open ClaimCore.Tests.ApplicationFixtures

let private roleExpectations =
    [
        Role.Owner, false, false
        Role.CaseReader, true, false
        Role.CaseRegistrar, true, false
        Role.CaseHandler, true, true
        Role.PaymentRecorder, true, false
        Role.DataSteward, true, false
        Role.Auditor, true, true
    ]

let private createReader fixture (admin: IActorClaimsCore) role =
    task {
        let principal =
            PrincipalKey.human "https://issuer.example/" "reader"
            |> Result.defaultWith failtest

        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 3) principal AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered
        let! granted = grant admin (id 4) principal role GrantTarget.Installation true
        acceptedAuthority granted
        return core fixture principal
    }

let private history (admin: IActorClaimsCore) =
    task {
        let original = openRequest 10 "PROBE-1"
        let! first = admin.Execute(original, CancellationToken.None)

        let amendment =
            request
                11
                "PROBE-1"
                1L
                (BusinessCommand.AmendRegistration
                    { registration with
                        ClaimantName = "Corrected Party"
                    })

        let! current = admin.Execute(amendment, CancellationToken.None)
        return original, accepted first, accepted current
    }

let private metadata (receipt: OperationReceipt) =
    match receipt.Scope with
    | ReceiptScope.Case value ->
        { receipt with
            Scope =
                ReceiptScope.Case
                    { value with
                        Snapshot = SnapshotDisclosure.Withheld
                    }
        }
    | _ -> failtest "Expected case receipt"

let private observed expected receipt outcome =
    match outcome with
    | QueryOutcome.Succeeded value ->
        Expect.equal (metadata value) (metadata receipt) "Disclosure changes no acceptance metadata"
        Expect.equal (caseReceipt value).Snapshot expected "Snapshot follows authoritative rights"
    | _ -> failtest "Expected operation observation"

let private fullPage erased (outcome: QueryOutcome<HistoryResultPage>) =
    match outcome with
    | QueryOutcome.Succeeded page ->
        for entry in page.Entries do
            match entry with
            | HistoryEntry.FullEntry receipt ->
                if erased then
                    Expect.equal
                        (caseReceipt receipt).Snapshot
                        SnapshotDisclosure.Erased
                        "Erasure is explicit"
                else
                    match (caseReceipt receipt).Snapshot with
                    | SnapshotDisclosure.Disclosed _ -> ()
                    | _ -> failtest "Full-history right should disclose retained snapshots"
            | _ -> failtest "FULL returns receipt views"
    | _ -> failtest "Expected full history"

let private summaryPage readable (outcome: QueryOutcome<HistoryResultPage>) =
    match outcome, readable with
    | QueryOutcome.Succeeded page, true ->
        Expect.equal page.Entries.Length 2 "Every operation ID remains visible"

        for entry in page.Entries do
            match entry with
            | HistoryEntry.SummaryEntry _ -> ()
            | _ -> failtest "Summary contains no snapshot"
    | QueryOutcome.Rejected Rejection.ResourceUnavailable, false -> ()
    | _ -> failtest "Unexpected summary disclosure"

let private historyRequest reference detail : HistoryRequest =
    {
        CaseReference = reference
        AfterCursor = None
        Limit = 50
        Detail = detail
    }

let private roleDisclosure role readable full =
    run (fun _ fixture ->
        task {
            let! admin = caseWorker fixture
            let! _, first, current = history admin
            let! reader = createReader fixture admin role
            let! older = reader.Observe(id 10, CancellationToken.None)
            let! latest = reader.Observe(id 11, CancellationToken.None)

            if readable then
                let expected =
                    if full then
                        (caseReceipt first).Snapshot
                    else
                        SnapshotDisclosure.Withheld

                observed expected first older
                observed (caseReceipt current).Snapshot current latest
            else
                Expect.equal
                    older
                    (QueryOutcome.Rejected Rejection.ResourceUnavailable)
                    "No history right"

                Expect.equal latest older "No current snapshot through observation"

            let! summary =
                reader.History(
                    (historyRequest "probe-1" HistoryDetail.Summary),
                    CancellationToken.None
                )

            summaryPage readable summary

            let! detailed =
                reader.History(
                    (historyRequest "PROBE-1" HistoryDetail.Full),
                    CancellationToken.None
                )

            if full then
                fullPage false detailed
            else
                Expect.equal
                    detailed
                    (QueryOutcome.Rejected Rejection.ResourceUnavailable)
                    "No full history"
        })

let private replayAndPreview (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        let! original, first, _ = history admin
        let! reader = createReader fixture admin Role.CaseRegistrar
        let! replay = reader.Execute(original, CancellationToken.None)

        let receipt =
            match replay with
            | ExecuteOutcome.AlreadyAccepted receipt -> receipt
            | _ -> failtest "Authorized exact replay missing"

        Expect.equal
            (metadata receipt)
            (metadata first)
            "Replay retains original identity and attribution"

        Expect.equal
            (caseReceipt receipt).Snapshot
            SnapshotDisclosure.Withheld
            "Superseded replay snapshot is withheld"

        let! preview = reader.Preview(original, CancellationToken.None)

        Expect.equal
            preview
            (PreviewOutcome.AlreadyAccepted receipt)
            "Preview acceptance follows the same disclosure rule"

        let! fresh = reader.Execute(openRequest 20 "FRESH-1", CancellationToken.None)

        match (accepted fresh |> caseReceipt).Snapshot with
        | SnapshotDisclosure.Disclosed _ -> ()
        | _ -> failtest "Fresh current acceptance should disclose"
    }

let private erasedHistory (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        let! _, first, _ = history admin
        let! reader = createReader fixture admin Role.Auditor

        let! steward =
            grant admin (id 5) owner Role.DataSteward GrantTarget.Installation true

        acceptedAuthority steward

        let! erase =
            admin.Execute(
                actRequest 12 "probe-1" 2L (CaseAct.Stewardship StewardshipAct.Erase),
                CancellationToken.None
            )

        Expect.equal
            (accepted erase |> caseReceipt).Snapshot
            SnapshotDisclosure.Erased
            "Erase acceptance is explicit"

        let! result = reader.Observe(id 10, CancellationToken.None)
        observed SnapshotDisclosure.Erased first result

        let! history =
            reader.History((historyRequest "PROBE-1" HistoryDetail.Full), CancellationToken.None)

        fullPage true history
    }

let private stewardshipReplay (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture
        let! _ = admin.Execute(openRequest 10 "PROBE-1", CancellationToken.None)
        let! reader = createReader fixture admin Role.DataSteward

        let original =
            actRequest 11 "PROBE-1" 1L (CaseAct.Stewardship(StewardshipAct.Void "Entered in error"))

        let! voided = reader.Execute(original, CancellationToken.None)
        let first = accepted voided

        let! reinstated =
            reader.Execute(
                actRequest
                    12
                    "PROBE-1"
                    2L
                    (CaseAct.Stewardship(StewardshipAct.Reinstate "Entry confirmed")),
                CancellationToken.None
            )

        accepted reinstated |> ignore
        let! replay = reader.Execute(original, CancellationToken.None)

        match replay with
        | ExecuteOutcome.AlreadyAccepted receipt ->
            Expect.equal (metadata receipt) (metadata first) "Stewardship metadata is stable"

            Expect.equal
                (caseReceipt receipt).Snapshot
                SnapshotDisclosure.Withheld
                "Steward cannot read superseded business snapshot"
        | _ -> failtest "Stewardship replay missing"
    }

let private commitSnapshot (_: MemoryLedgerStore.Store) (fixture: Fixture) =
    task {
        let! admin = caseWorker fixture

        let principal =
            PrincipalKey.human "https://issuer.example/" "registrar"
            |> Result.defaultWith failtest

        let! registered =
            admin.ExecuteAuthority(
                authorityRequest (id 3) principal AuthorityAct.RegisterActor,
                CancellationToken.None
            )

        acceptedAuthority registered

        let! granted =
            grant admin (id 4) principal Role.CaseRegistrar GrantTarget.Installation true

        acceptedAuthority granted

        let afterWrite () =
            task {
                let amendment =
                    request
                        11
                        "PROBE-1"
                        1L
                        (BusinessCommand.AmendRegistration
                            { registration with
                                ClaimantName = "Later Party"
                            })

                let! amended = admin.Execute(amendment, CancellationToken.None)
                accepted amended |> ignore

                let! revoked =
                    grant admin (id 5) principal Role.CaseRegistrar GrantTarget.Installation false

                acceptedAuthority revoked
            }

        let store = AfterCaseWriteStore(fixture.Store, afterWrite)

        let actor = Core.create installation (store :> ILedgerStore) calendar principal

        let! result = actor.Execute(openRequest 10 "PROBE-1", CancellationToken.None)
        let receipt = accepted result
        Expect.equal store.ReadCalls 0 "No state is fetched after commit to shape the receipt"

        Expect.equal
            (disclosedFields receipt).ClaimantName
            registration.ClaimantName
            "Disclosure uses the write's locked context"

        Expect.equal receipt.RecordedBy (id 3) "Original executor"
        let! denied = (core fixture principal).Get("PROBE-1", CancellationToken.None)

        Expect.equal
            denied
            (QueryOutcome.Rejected Rejection.ResourceUnavailable)
            "Later reads use revoked authority"
    }

[<Tests>]
let tests =
    testList
        "receipt snapshot disclosure"
        [
            for role, readable, full in roleExpectations do
                testCaseAsync
                    (Roles.token role + " current and superseded observation and history")
                    (roleDisclosure role readable full)
            testCaseAsync
                "execute replay and preview ALREADY_ACCEPTED withhold superseded snapshots"
                (run replayAndPreview)
            testCaseAsync
                "erasure remains distinct from withholding in observation and full history"
                (run erasedHistory)
            testCaseAsync
                "stewardship replay cannot bypass full-history restrictions"
                (run stewardshipReplay)
            testCaseAsync
                "receipts retain locked disclosure when authority and revision change before delivery"
                (run commitSnapshot)
        ]
