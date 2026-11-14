module ClaimCore.Tests.ReceiptDisclosureTests

open ClaimCore.TestSupport

open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.Tests.DecisionFixtures


let private receipt people current entry =
    LedgerEntries.validateOperation installation entry
    |> Result.bind (
        LedgerEntries.receiptOperation
            (Inputs.workBasis)
            owner
            people
            (Some(Inputs.actorReference entry.ActorId))
            (ReceiptCurrent.Case(Some current))
    )
    |> Fixtures.accepted

let private disclosure () =
    let request, entry, current, _ = created 200 "PURE-DISCLOSURE"
    let reader = actor owner ((CaseFixtures.actorId (id 1))) [ Role.CaseReader ] []

    Expect.isTrue
        (match (receipt reader current entry).Subject with
         | ReceiptSubject.Case {
                                   Snapshot = SnapshotDisclosure.Disclosed _
                               } -> true
         | _ -> false)
        "Current read discloses the current snapshot"

    let superseded =
        { current with
            Snapshot =
                { current.Snapshot with
                    Revision = CaseFixtures.revision 2L
                }
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

    let registrar =
        actor owner ((CaseFixtures.actorId (id 1))) [ Role.CaseRegistrar ] []

    let view = locked registrar (Some superseded) (Some entry)

    match
        CaseOperations.decide
            (caseContext
                (DecisionFixtures.services installation calendar owner)
                SubjectExecutionMode.Preview
                view.Read)
            (DecisionFixtures.services installation calendar owner)
            (CaseFixtures.command request)
            view.Read
    with
    | Verdict.Replay value ->
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
                    Revision = CaseFixtures.revision 2L
                    State = CaseState.Erased
                }
        }

    let entry =
        OperationRoot.restore
            entry.Root.Header
            (OperationRoot.commandBytes entry.Root)
            (OperationRoot.effectBytes entry.Root)
            (entry.Root.Structure.Compartments |> List.map Compartment.withoutContent)
            entry.Root.Hash
        |> Fixtures.accepted
        |> RetainedOperation.decode
        |> Fixtures.accepted

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
            ((CaseFixtures.actorId (id 1)))
            []
            [ caseA.CaseId, [ Role.CaseHandler ]; caseB.CaseId, [ Role.CaseReader ] ]

    let request =
        { original with
            CaseReference = CaseReference.format caseA.Snapshot.Reference
            ExpectedRevision = 1L
            Act = (AuthoredCaseAct.Bare BareAct.Close)
        }

    let read = (locked both (Some caseA) (Some entry)).Read

    match
        CaseOperations.decide
            (caseContext
                (DecisionFixtures.services installation calendar owner)
                SubjectExecutionMode.Preview
                read)
            (DecisionFixtures.services installation calendar owner)
            (CaseFixtures.command request)
            read
    with
    | Verdict.Refuse Refusal.IdempotencyConflict -> ()
    | _ -> failtest "Visible existing-operation subject reveals only conflict"

    let narrowed =
        { read with
            Actor =
                { both with
                    Cases = Map.ofList [ caseA.CaseId, [ Role.CaseHandler ] ]
                }
        }

    match
        CaseOperations.decide
            (caseContext
                (DecisionFixtures.services installation calendar owner)
                SubjectExecutionMode.Preview
                narrowed)
            (DecisionFixtures.services installation calendar owner)
            (CaseFixtures.command request)
            narrowed
    with
    | Verdict.Refuse(Refusal.Access AccessRefusal.ResourceUnavailable) -> ()
    | _ -> failtest "Missing accepted-operation subject must retain privacy"

let private roles role readable full () =
    let _, entry, current, _ = created 204 "ROLE-DISCLOSURE"
    let people = actor owner ((CaseFixtures.actorId (id 1))) [ role ] []

    Expect.equal
        (LedgerEntries.canObserve owner people (LedgerEntries.primary entry).Subject)
        readable
        "Current read authorization"

    if readable then
        Expect.isTrue
            (match (caseReceipt (receipt people current entry)).Snapshot with
             | SnapshotDisclosure.Disclosed _ -> true
             | _ -> false)
            "Current snapshot disclosed"

        let later =
            { current with
                Snapshot =
                    { current.Snapshot with
                        Revision = CaseFixtures.revision 2L
                    }
            }

        let shown = (caseReceipt (receipt people later entry)).Snapshot

        Expect.equal
            (match shown with
             | SnapshotDisclosure.Disclosed _ -> true
             | _ -> false)
            full
            "Superseded disclosure requires full history"



let private stewardship () =
    let _, _, current, _ = created 205 "STEWARD-REPLAY"
    let steward = actor owner ((CaseFixtures.actorId (id 1))) [ Role.DataSteward ] []

    let request =
        actRequest
            206
            (CaseReference.format current.Snapshot.Reference)
            1L
            (AuthoredCaseAct.Void "Entered in error")

    let draft, projection, first =
        decide request (locked steward (Some current) None) |> appended

    let voided =
        {
            StoredCase.CaseId = projection.Rows.Snapshot.CaseId
            Snapshot = projection.Rows.Snapshot
            Index = projection.Index
            SnapshotBytes =
                ClaimCore.RecordFormat.CaseRecord.encodeSnapshot projection.Rows.Snapshot
        }

    let restore =
        actRequest
            207
            (CaseReference.format current.Snapshot.Reference)
            2L
            (AuthoredCaseAct.Reinstate "Confirmed")

    let _, projection, _ =
        decide restore (locked steward (Some voided) None) |> appended

    let restored =
        {
            StoredCase.CaseId = projection.Rows.Snapshot.CaseId
            Snapshot = projection.Rows.Snapshot
            Index = projection.Index
            SnapshotBytes =
                ClaimCore.RecordFormat.CaseRecord.encodeSnapshot projection.Rows.Snapshot
        }

    match decide request (locked steward (Some restored) (Some draft)) with
    | Verdict.Replay replay ->
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
                    (Role.token role + " current and superseded history disclosure")
                    (roles role readable full)
            testCase "stewardship replay retains full-history restrictions" stewardship
            testCase
                "current superseded and preview replay disclosure use current grants"
                disclosure
            testCase "erased payload remains distinct from withheld data" erased
            testCase
                "operation identity loads people for both requested and accepted cases"
                foreignScope
        ]
