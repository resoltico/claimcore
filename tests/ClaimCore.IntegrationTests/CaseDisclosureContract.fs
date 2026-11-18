module ClaimCore.TestSupport.CaseDisclosureContract

open ClaimCore.TestSupport

open ClaimCore.Wire
open ClaimCore.Presentation
open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.RecordFormat
open ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport.StoreContractReads

let internal erasureReadback fixture (actor: ServiceContext) =
    task {
        let! rows = caseTimeline fixture "CONTRACT-001" None 50
        let page = storedPage rows

        match page.Case with
        | Some {
                   Snapshot = { State = CaseState.Erased }
               } -> ()
        | _ -> failtest "Missing erased projection"

        Expect.equal page.Entries.Length 2 "Full accepted metadata remains"

        Expect.isTrue
            (page.Entries
             |> List.forall (fun entry ->
                 entry.Operation.Root.Structure.Compartments
                 |> List.forall (Compartment.content >> Option.isNone)))
            "Only erasable columns are cleared"

        let! other =
            fixture.Store.Read
                {
                    Work = fun tx -> FamilyReads.findCaseByReference tx "CONTRACT-002"
                    CancellationToken = CancellationToken.None
                }

        match other with
        | Ok(Some {
                      Snapshot = {
                                     State = (CaseState.Active _ | CaseState.Voided _)
                                 }
                  }) -> ()
        | _ -> failtest "Other case changed"

        let! replay =
            (Service.executeSubmission
                actor
                (((request
                    (fixture)
                    (10)
                    ("CONTRACT-001")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                (CancellationToken.None))

        Expect.equal
            replay.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Erased requests cannot replay"

        let! reopen =
            (Service.executeSubmission
                actor
                (((request
                    (fixture)
                    (13)
                    ("contract-001")
                    (0L)
                    "case.open"
                    (ClaimCore.TestSupport.Submissions.fields (
                        RegistrationInput.values (registration)
                    )))))
                (CancellationToken.None))

        Expect.equal
            reopen.Outcome
            (ExecuteResult.Rejected(Refusal.Case CaseRefusal.CaseAlreadyExists))
            "Folded reference remains reserved"
    }

let internal caseErasure fixture =
    task {
        let! actor = caseWorker fixture

        let! granted =
            grant
                actor
                (id fixture 3)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted

        for n, reference in [ 10, "CONTRACT-001"; 11, "CONTRACT-002" ] do
            let! opened =
                (Service.executeSubmission
                    actor
                    (((request
                        (fixture)
                        (n)
                        (reference)
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (registration)
                        )))))
                    (CancellationToken.None))

            accepted opened |> ignore

        let erase = request (fixture) (12) ("CONTRACT-001") (1L) "case.erase" (Map.empty)

        let! result = (Service.executeSubmission actor ((erase)) (CancellationToken.None))

        Expect.equal
            (accepted result |> caseReceipt).Snapshot
            SnapshotDisclosure.Erased
            "Erasure is distinct from withholding"

        do! erasureReadback fixture actor
    }

let internal caseVisibility fixture =
    task {
        let! actor = caseWorker fixture

        for n, reference in [ 10, "C"; 11, "A"; 12, "B" ] do
            let! result =
                (Service.executeSubmission
                    actor
                    (((request
                        (fixture)
                        (n)
                        (reference)
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (registration)
                        )))))
                    (CancellationToken.None))

            accepted result |> ignore

        let! first = listCases fixture CaseVisibility.Every None 2
        let first = storedPage first

        Expect.equal
            (first.Items
             |> List.map (fun record -> CaseReference.format record.Case.Snapshot.Reference))
            [ "A"; "B" ]
            "Reference-key order"

        Expect.equal
            first.Next
            (Some(ClaimCore.RecordFormat.CaseListPosition.Reference "B"))
            "Last visible key"

        let! principal = assignedReader fixture actor

        let! only = casePage fixture principal CaseVisibility.Scoped None 1

        Expect.equal
            ((storedPage only).Items
             |> List.map (fun record -> CaseReference.format record.Case.Snapshot.Reference))
            [ "C" ]
            "Visibility precedes limit"

        let! next =
            listCases
                fixture
                CaseVisibility.Every
                (Some(ClaimCore.RecordFormat.CaseListPosition.Reference "B"))
                2

        Expect.equal
            ((storedPage next).Items
             |> List.map (fun record -> CaseReference.format record.Case.Snapshot.Reference))
            [ "C" ]
            "Continuation"
    }

let internal foldedOrdering fixture =
    task {
        let! actor = caseWorker fixture

        for n, reference in [ 20, "Ref-b"; 21, "ref-A"; 22, "REF-C" ] do
            let! result =
                (Service.executeSubmission
                    actor
                    (((request
                        (fixture)
                        (n)
                        (reference)
                        (0L)
                        "case.open"
                        (ClaimCore.TestSupport.Submissions.fields (
                            RegistrationInput.values (registration)
                        )))))
                    (CancellationToken.None))

            accepted result |> ignore

        let! first = listCases fixture CaseVisibility.Every None 1
        let first = storedPage first

        Expect.equal
            (first.Items
             |> List.map (fun record -> CaseReference.format record.Case.Snapshot.Reference))
            [ "ref-A" ]
            "Authored spelling with ASCII-key order"

        Expect.equal
            first.Next
            (Some(ClaimCore.RecordFormat.CaseListPosition.Reference "REF-A"))
            "Continuation is folded"

        let! second = listCases fixture CaseVisibility.Every first.Next 1

        Expect.equal
            ((storedPage second).Items
             |> List.map (fun record -> CaseReference.format record.Case.Snapshot.Reference))
            [ "Ref-b" ]
            "Continuation uses the same order"
    }
