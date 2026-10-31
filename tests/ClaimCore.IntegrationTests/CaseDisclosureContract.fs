module ClaimCore.TestSupport.CaseDisclosureContract

open ClaimCore.TestSupport

open ClaimCore.Protocol
open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Application
open ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport.StoreContractReads

let internal erasureReadback fixture (actor: ServiceContext) =
    task {
        let! rows = history fixture "CONTRACT-001" (CaseFixtures.revision (0L)) 50
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
                 entry.Payload.IsNone
                 && entry.PayloadNonce.IsNone
                 && entry.PayloadCommitment.Length = 32))
            "Only erasable columns are cleared"

        let! other =
            inspectCase fixture (fixture.Owner) (None) ("CONTRACT-002") (CancellationToken.None)

        match other with
        | Ok {
                 Case = Some {
                                 Snapshot = {
                                                State = (CaseState.Active _ | CaseState.Voided _)
                                            }
                             }
             } -> ()
        | _ -> failtest "Other case changed"

        let! replay =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request 10 "CONTRACT-001" 0L (AuthoredCaseAct.Open registration)
                ))
                (CancellationToken.None))

        Expect.equal
            replay.Outcome
            (ExecuteResult.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Erased requests cannot replay"

        let! reopen =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request 13 "contract-001" 0L (AuthoredCaseAct.Open registration)
                ))
                (CancellationToken.None))

        Expect.equal
            reopen.Outcome
            (ExecuteResult.Rejected((Refusal.Case CaseRefusal.CaseErased)))
            "Folded reference remains reserved"
    }

let internal caseErasure fixture =
    task {
        let! actor = caseWorker fixture

        let! granted =
            grant
                actor
                (id 3)
                fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedAuthority granted

        for n, reference in [ 10, "CONTRACT-001"; 11, "CONTRACT-002" ] do
            let! opened =
                (Service.execute
                    actor
                    (ClaimCore.TestSupport.Inputs.caseOperation (
                        request n reference 0L (AuthoredCaseAct.Open registration)
                    ))
                    (CancellationToken.None))

            accepted opened |> ignore

        let erase = actRequest 12 "CONTRACT-001" 1L ((AuthoredCaseAct.Bare BareAct.Erase))

        let! result =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation erase)
                (CancellationToken.None))

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
                (Service.execute
                    actor
                    (ClaimCore.TestSupport.Inputs.caseOperation (
                        request n reference 0L (AuthoredCaseAct.Open registration)
                    ))
                    (CancellationToken.None))

            accepted result |> ignore

        let! first = listCases fixture CaseVisibility.Every None 2
        let first = storedPage first

        Expect.equal
            (first.Items
             |> List.map (fun record -> CaseReference.format record.Snapshot.Reference))
            [ "A"; "B" ]
            "Reference-key order"

        Expect.equal
            first.Next
            (Some(ClaimCore.RecordFormat.CaseListPosition.Reference "B"))
            "Last visible key"

        let! principal = assignedReader actor

        let! only = casePage fixture principal CaseVisibility.Assigned None 1

        Expect.equal
            ((storedPage only).Items
             |> List.map (fun record -> CaseReference.format record.Snapshot.Reference))
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
             |> List.map (fun record -> CaseReference.format record.Snapshot.Reference))
            [ "C" ]
            "Continuation"
    }

let internal foldedOrdering fixture =
    task {
        let! actor = caseWorker fixture

        for n, reference in [ 20, "Ref-b"; 21, "ref-A"; 22, "REF-C" ] do
            let! result =
                (Service.execute
                    actor
                    (ClaimCore.TestSupport.Inputs.caseOperation (
                        request n reference 0L (AuthoredCaseAct.Open registration)
                    ))
                    (CancellationToken.None))

            accepted result |> ignore

        let! first = listCases fixture CaseVisibility.Every None 1
        let first = storedPage first

        Expect.equal
            (first.Items
             |> List.map (fun record -> CaseReference.format record.Snapshot.Reference))
            [ "ref-A" ]
            "Authored spelling with ASCII-key order"

        Expect.equal
            first.Next
            (Some(ClaimCore.RecordFormat.CaseListPosition.Reference "REF-A"))
            "Continuation is folded"

        let! second = listCases fixture CaseVisibility.Every first.Next 1

        Expect.equal
            ((storedPage second).Items
             |> List.map (fun record -> CaseReference.format record.Snapshot.Reference))
            [ "Ref-b" ]
            "Continuation uses the same order"
    }
