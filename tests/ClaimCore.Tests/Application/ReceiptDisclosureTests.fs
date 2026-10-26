module ClaimCore.Tests.ReceiptDisclosureTests

open Expecto
open ClaimCore.Contract
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures

let private receipt authority current entry =
    LedgerEntries.validate installation entry
    |> Result.bind (LedgerEntries.receipt owner authority (Some current))
    |> Fixtures.accepted

let private disclosure () =
    let request, entry, current, _ = created 200 "PURE-DISCLOSURE"
    let reader = actor owner (id 1) [ Role.CaseReader ] []

    Expect.isTrue
        (match (receipt reader current entry).Subject with
         | ReceiptSubject.Case {
                                   Snapshot = SnapshotDisclosure.Disclosed _
                               } -> true
         | _ -> false)
        "Current read discloses the current snapshot"

    let superseded =
        { current with
            Snapshot = { current.Snapshot with Revision = 2L }
        }

    Expect.equal
        (caseReceipt (receipt reader superseded entry)).Snapshot
        SnapshotDisclosure.Withheld
        "Superseded data needs full history"

    let privileged = receipt worker superseded entry

    Expect.isTrue
        (match (caseReceipt privileged).Snapshot with
         | SnapshotDisclosure.Disclosed _ -> true
         | _ -> false)
        "Full history can disclose the superseded snapshot"

    let registrar = actor owner (id 1) [ Role.CaseRegistrar ] []
    let view = locked registrar (Some superseded) (Some entry)

    match CaseDecisions.review installation calendar owner request view.Read with
    | Decision.Replay value ->
        Expect.equal
            (caseReceipt value).Snapshot
            SnapshotDisclosure.Withheld
            "Preview replay shares disclosure"
    | _ -> failtest "Expected replay disclosure"

let private erased () =
    let _, entry, current, _ = created 201 "PURE-ERASED"

    let erased =
        { current with
            Snapshot =
                { current.Snapshot with
                    Revision = 2L
                    State = CaseState.Erased
                }
        }

    let entry =
        { entry with
            Payload = None
            PayloadNonce = None
        }

    Expect.equal
        (caseReceipt (receipt worker erased entry)).Snapshot
        SnapshotDisclosure.Erased
        "Erased is not withheld"

let private foreignScope () =
    let original, entry, caseB, _ = created 202 "SCOPE-B"
    let _, _, caseA, _ = created 203 "SCOPE-A"

    let both =
        actor
            owner
            (id 1)
            []
            [ caseA.CaseId, [ Role.CaseHandler ]; caseB.CaseId, [ Role.CaseReader ] ]

    let request =
        { original with
            CaseReference = caseA.Snapshot.Reference
            ExpectedRevision = 1L
            Act = CaseAct.Close
        }

    let read = (locked both (Some caseA) (Some entry)).Read

    match CaseDecisions.review installation calendar owner request read with
    | Decision.Refuse Refusal.IdempotencyConflict -> ()
    | _ -> failtest "Visible existing-operation subject reveals only conflict"

    let narrowed =
        { read with
            Actor =
                { both with
                    Cases = Map.ofList [ caseA.CaseId, [ Role.CaseHandler ] ]
                }
        }

    match CaseDecisions.review installation calendar owner request narrowed with
    | Decision.Refuse Refusal.ResourceUnavailable -> ()
    | _ -> failtest "Missing accepted-operation subject must retain privacy"

let private roles role readable full () =
    let _, entry, current, _ = created 204 "ROLE-DISCLOSURE"
    let authority = actor owner (id 1) [ role ] []

    Expect.equal
        (LedgerEntries.canObserve owner authority entry.Subject)
        readable
        "Current read authorization"

    if readable then
        Expect.isTrue
            (match (caseReceipt (receipt authority current entry)).Snapshot with
             | SnapshotDisclosure.Disclosed _ -> true
             | _ -> false)
            "Current snapshot disclosed"

        let later =
            { current with
                Snapshot = { current.Snapshot with Revision = 2L }
            }

        let shown = (caseReceipt (receipt authority later entry)).Snapshot

        Expect.equal
            (match shown with
             | SnapshotDisclosure.Disclosed _ -> true
             | _ -> false)
            full
            "Superseded disclosure requires full history"

        let read: HistoryReadView =
            {
                Actor = authority
                Case = Some later
                ObservedAt = instant
            }

        let query: HistoryRequest =
            {
                CaseReference = current.Snapshot.Reference
                AfterCursor = None
                Limit = 50
                Detail = HistoryDetail.Full
            }

        Expect.equal
            (Queries.selectHistory owner query read |> Result.isOk)
            full
            "Full-history selection shares policy"

let private stewardship () =
    let _, _, current, _ = created 205 "STEWARD-REPLAY"
    let steward = actor owner (id 1) [ Role.DataSteward ] []

    let request =
        actRequest 206 current.Snapshot.Reference 1L (CaseAct.Void "Entered in error")

    let draft, projection, first =
        decide request (locked steward (Some current) None) |> appended

    let voided =
        match projection with
        | CaseProjection.Update(id, snapshot) -> { CaseId = id; Snapshot = snapshot }
        | _ -> failtest "Update"

    let restore =
        actRequest 207 current.Snapshot.Reference 2L (CaseAct.Reinstate "Confirmed")

    let _, projection, _ =
        decide restore (locked steward (Some voided) None) |> appended

    let restored =
        match projection with
        | CaseProjection.Update(id, snapshot) -> { CaseId = id; Snapshot = snapshot }
        | _ -> failtest "Update"

    match decide request (locked steward (Some restored) (Some(LedgerEntries.stored draft))) with
    | Decision.Replay replay ->
        Expect.equal replay.RecordedBy first.RecordedBy "Original executor"
        Expect.equal replay.Position first.Position "Original position"

        Expect.equal
            (caseReceipt replay).Snapshot
            SnapshotDisclosure.Withheld
            "Steward cannot read superseded business snapshot"
    | _ -> failtest "Stewardship replay missing"

[<Tests>]
let tests =
    testList
        "pure receipt disclosure"
        [
            for role, readable, full in
                [
                    Role.Owner, false, false
                    Role.CaseReader, true, false
                    Role.CaseRegistrar, true, false
                    Role.CaseHandler, true, true
                    Role.PaymentRecorder, true, false
                    Role.DataSteward, true, false
                    Role.Auditor, true, true
                ] do
                testCase
                    (RoleTokens.token role + " current and superseded history disclosure")
                    (roles role readable full)
            testCase "stewardship replay retains full-history restrictions" stewardship
            testCase
                "current superseded and preview replay disclosure use current grants"
                disclosure
            testCase "erased payload remains distinct from withheld data" erased
            testCase
                "operation identity loads authority for both requested and accepted cases"
                foreignScope
        ]
