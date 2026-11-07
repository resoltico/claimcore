module ClaimCore.TestSupport.LedgerWriteContract

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
open ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport.StoreContractReads

let internal genesis fixture =
    task {
        let mutable calls = 0

        let! result =
            fixture.Store.Write(
                {
                    WriteOperation.Work =
                        (fun tx ->
                            task {
                                calls <- calls + 1

                                let! view =
                                    ExecutionFacts.target
                                        (core fixture fixture.Owner)
                                        tx
                                        (Authority.factsNeeded (
                                            AuthorityAct.ProvisionOwner(
                                                ActorProfile.parse Inputs.ownerProfile
                                                |> Result.defaultWith (string >> failtest)
                                            )
                                        ))
                                        ((CaseFixtures.operationId (id fixture 1)))
                                        fixture.Owner
                                        []

                                Expect.equal
                                    tx.Head.Sequence
                                    (CaseFixtures.sequence 0L)
                                    "Genesis head"

                                Expect.isTrue
                                    (tx.Head.EntryHash = EntryHash.zero)
                                    "Zero previous hash"

                                Expect.isNone view.Actor.Actor "No actor before provisioning"

                                return!
                                    AuthorityExecution.provision
                                        (core fixture fixture.Owner)
                                        ((CaseFixtures.operationId (id fixture 1)))
                                        Inputs.ownerProfile
                                        {
                                            Head = tx.Head

                                            NewPayloadNonce = tx.NewPayloadNonce
                                        }
                                        view
                                    |> ExecutionFacts.persistOperation tx tx.ProjectAuthority
                            })
                    CancellationToken = CancellationToken.None
                }
            )

        match result with
        | Ok(Verdict.Proceed receipt) ->
            Expect.equal receipt.Position.Sequence (CaseFixtures.sequence 2L) "First sequence"
        | _ -> failtest "Expected append"

        Expect.equal calls 1 "One decision"
        let! actor = inspectAuthority fixture (fixture.Owner) (CancellationToken.None)

        match actor with
        | Ok({ Actor = Some _ } as authority) ->
            Expect.equal authority.Installation [ Role.Owner ] "Only the owner role is provisioned"
        | _ -> failtest "Missing owner projection"

        let! entry =
            inspectOperation
                fixture
                (fixture.Owner)
                ((CaseFixtures.operationId (id fixture 1)))
                (CancellationToken.None)

        match entry with
        | Ok { ExistingOperation = Some entry } ->
            Expect.equal entry.Position.Sequence (CaseFixtures.sequence 2L) "Readable acceptance"
        | _ -> failtest "Missing accepted entry"
    }

let internal replayAndRefusal fixture =
    task {
        let! actor = caseWorker fixture

        let original =
            request fixture 10 "CONTRACT-001" 0L (AuthoredCaseAct.Open registration)

        let! result =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation original)
                (CancellationToken.None))

        let receipt = accepted result

        Expect.equal
            receipt.Position.Sequence
            (CaseFixtures.sequence 5L)
            "Owner profile effect and ordinary operations share the physical chain"

        let! replay =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation original)
                (CancellationToken.None))

        expectReceipt (replayReceipt replay) receipt "Original acceptance with current observation"

        let! stale =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request fixture 11 "CONTRACT-001" 0L (AuthoredCaseAct.Bare BareAct.Close)
                ))
                (CancellationToken.None))

        match stale.Outcome with
        | ExecuteResult.Rejected(Refusal.RevisionConflict conflict) ->
            Expect.equal (Revision.value conflict.ActualRevision) 1L "No stale acceptance"
            Expect.isSome conflict.Changes "The authorized snapshot supplies conflict history"
        | other -> failtestf "Expected revision conflict, got %A" other

        let! view =
            inspectCase fixture (fixture.Owner) (None) ("CONTRACT-001") (CancellationToken.None)

        match view with
        | Ok { Case = Some record } ->
            Expect.equal
                (caseReceipt receipt).Snapshot
                (SnapshotDisclosure.Disclosed(LiveCase.ofState record.Snapshot.State |> Option.get))
                "Projection and receipt agree"
        | _ -> failtest "Missing case"

        let! rows = caseTimeline fixture "CONTRACT-001" None 50
        Expect.equal (storedPage rows).Entries.Length 1 "Replay/refusal append nothing"
    }

let internal lockedView fixture =
    task {
        let! actor = caseWorker fixture

        let original =
            request fixture 10 "CONTRACT-001" 0L (AuthoredCaseAct.Open registration)

        let! opened =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation original)
                (CancellationToken.None))

        let first = accepted opened
        let mutable calls = 0

        let! result =
            fixture.Store.Write(
                {
                    WriteOperation.Work =
                        (fun tx ->
                            task {
                                let! view =
                                    ExecutionFacts.case
                                        (core fixture fixture.Owner)
                                        tx
                                        (Some(CaseFixtures.operationId original.OperationId))
                                        (CaseSelector.Reference original.CaseReference)

                                calls <- calls + 1

                                Expect.equal
                                    tx.Head.Sequence
                                    first.Position.Sequence
                                    "Current head"

                                Expect.equal
                                    tx.Head.EntryHash
                                    first.Position.EntryHash
                                    "Previous accepted link"

                                Expect.equal
                                    (view.Case
                                     |> Option.map (fun record -> record.Snapshot.Revision))
                                    (Some(CaseFixtures.revision 1L))
                                    "Current case"

                                Expect.equal
                                    (view.ExistingOperation |> Option.map _.OperationId)
                                    (Some(CaseFixtures.operationId original.OperationId))
                                    "Existing identity in the same view"

                                return!
                                    CaseExecution.execute
                                        (core fixture fixture.Owner)
                                        (CaseFixtures.command original)
                                        {
                                            Head = tx.Head
                                            Nonce = tx.NewPayloadNonce()
                                        }
                                        view
                                    |> ExecutionFacts.persist tx tx.ProjectCase

                            })
                    CancellationToken = CancellationToken.None
                }
            )

        match result with
        | Ok(Verdict.Replay receipt) -> expectReceipt receipt first "Locked replay"
        | _ -> failtest "Expected replay"

        Expect.equal calls 1 "Callback called once"
    }

let internal caseUpdate fixture =
    task {
        let! actor = caseWorker fixture

        let! opened =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request fixture 10 "CONTRACT-001" 0L (AuthoredCaseAct.Open registration)
                ))
                (CancellationToken.None))

        accepted opened |> ignore

        let! closed =
            (Service.execute
                actor
                (ClaimCore.TestSupport.Inputs.caseOperation (
                    request fixture 11 "CONTRACT-001" 1L (AuthoredCaseAct.Bare BareAct.Close)
                ))
                (CancellationToken.None))

        let receipt = accepted closed
        Expect.equal (caseReceipt receipt).Revision (CaseFixtures.revision (2L)) "One revision"
        Expect.equal (disclosedFields receipt).Status CaseStatus.Closed "Closure stored"
        let! rows = caseTimeline fixture "CONTRACT-001" None 1
        let page = storedPage rows
        Expect.equal page.Entries.Length 1 "Bounded page"

        Expect.equal
            page.Next
            (Some page.Entries.Head.Sequence)
            "Newest included sequence owns continuation"

        let revisions (entries: StoredEntry list) =
            entries
            |> List.map (fun entry ->
                match entry.Subject with
                | EntryTarget.Case(_, revision, _, _) -> Revision.value revision
                | _ -> failtest "Expected case history")

        Expect.equal (revisions page.Entries) [ 2L ] "First page contains newest revision"
        let! continued = caseTimeline fixture "CONTRACT-001" page.Next 1
        let continued = storedPage continued

        Expect.equal
            (revisions continued.Entries)
            [ 1L ]
            "Continuation returns preceding revision without duplicates"

        Expect.isNone continued.Next "Last page has no continuation"

        let! exhausted =
            caseTimeline fixture "CONTRACT-001" (Some continued.Entries.Head.Sequence) 1

        Expect.isEmpty (storedPage exhausted).Entries "Continuation below opening revision is empty"

        let! operation =
            inspectOperation
                fixture
                (fixture.Owner)
                ((CaseFixtures.operationId (id fixture 11)))
                (CancellationToken.None)

        match operation with
        | Ok {
                 ExistingOperation = Some entry
                 Case = Some record
             } ->
            Expect.equal entry.Position.Sequence receipt.Position.Sequence "Operation position"
            Expect.equal record.Snapshot.Revision (CaseFixtures.revision (2L)) "Current projection"
        | _ -> failtest "Missing accepted update"
    }
