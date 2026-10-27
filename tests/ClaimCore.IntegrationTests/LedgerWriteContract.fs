module ClaimCore.TestSupport.LedgerWriteContract

open ClaimCore.TestSupport

open ClaimCore.Protocol
open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Domain
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
                                        (id 1)
                                        fixture.Owner
                                        None

                                Expect.equal tx.Head.Sequence 0L "Genesis head"

                                Expect.isTrue
                                    (tx.Head.EntryHash = EntryHash.zero)
                                    "Zero previous hash"

                                Expect.isNone view.Actor.Actor "No actor before provisioning"

                                return!
                                    AuthorityExecution.provision
                                        (core fixture fixture.Owner)
                                        (id 1)
                                        {
                                            Head = tx.Head
                                            Fenced = false
                                            Nonce = tx.Nonce
                                        }
                                        view
                                    |> ExecutionFacts.persist tx tx.ProjectAuthority
                            })
                    CancellationToken = CancellationToken.None
                }
            )

        match result with
        | Ok(Verdict.Proceed receipt) -> Expect.equal receipt.Position.Sequence 1L "First sequence"
        | _ -> failtest "Expected append"

        Expect.equal calls 1 "One decision"
        let! actor = inspectAuthority fixture (fixture.Owner) (CancellationToken.None)

        match actor with
        | Ok({ Actor = Some _ } as authority) ->
            Expect.equal authority.Installation [ Role.Owner ] "Only the owner role is provisioned"
        | _ -> failtest "Missing owner projection"

        let! entry = inspectOperation fixture (fixture.Owner) (id 1) (CancellationToken.None)

        match entry with
        | Ok { ExistingOperation = Some entry } ->
            Expect.equal entry.Sequence 1L "Readable acceptance"
        | _ -> failtest "Missing accepted entry"
    }

let internal replayAndRefusal fixture =
    task {
        let! actor = caseWorker fixture
        let original = request 10 "CONTRACT-001" 0L (AuthoredCaseAct.Open registration)

        let! result =
            (Service.execute actor (OperationInput.Case original) (CancellationToken.None))

        let receipt = accepted result

        Expect.equal
            receipt.Position.Sequence
            4L
            "One entry per authority operation and case operation"

        let! replay =
            (Service.execute actor (OperationInput.Case original) (CancellationToken.None))

        Expect.equal replay.Outcome (ExecuteResult.AlreadyAccepted receipt) "Original receipt"

        let! stale =
            (Service.execute
                actor
                (OperationInput.Case(request 11 "CONTRACT-001" 0L AuthoredCaseAct.Close))
                (CancellationToken.None))

        Expect.equal
            stale.Outcome
            (ExecuteResult.Rejected((Refusal.RevisionConflict 1L)))
            "No stale acceptance"

        let! view =
            inspectCase fixture (fixture.Owner) (None) ("CONTRACT-001") (CancellationToken.None)

        match view with
        | Ok { Case = Some record } ->
            Expect.equal
                (caseReceipt receipt).Snapshot
                (SnapshotDisclosure.Disclosed(LiveCase.ofState record.Snapshot.State |> Option.get))
                "Projection and receipt agree"
        | _ -> failtest "Missing case"

        let! rows = history fixture "CONTRACT-001" 0L 50
        Expect.equal (storedPage rows).Entries.Length 1 "Replay/refusal append nothing"
    }

let internal lockedView fixture =
    task {
        let! actor = caseWorker fixture
        let original = request 10 "CONTRACT-001" 0L (AuthoredCaseAct.Open registration)

        let! opened =
            (Service.execute actor (OperationInput.Case original) (CancellationToken.None))

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
                                        (Some original.OperationId)
                                        original.CaseReference

                                let! fenced =
                                    ExecutionFacts.fenced tx view.Actor original.OperationId

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
                                    (Some 1L)
                                    "Current case"

                                Expect.equal
                                    (view.ExistingOperation |> Option.map _.OperationId)
                                    (Some original.OperationId)
                                    "Existing identity in the same view"

                                return!
                                    CaseExecution.execute
                                        (core fixture fixture.Owner)
                                        (CaseFixtures.command original)
                                        {
                                            Head = tx.Head
                                            Fenced = fenced
                                            Nonce = tx.Nonce
                                        }
                                        view
                                    |> ExecutionFacts.persist tx tx.ProjectCase

                            })
                    CancellationToken = CancellationToken.None
                }
            )

        match result with
        | Ok(Verdict.Replay receipt) -> Expect.equal receipt first "Locked replay"
        | _ -> failtest "Expected replay"

        Expect.equal calls 1 "Callback called once"
    }

let internal caseUpdate fixture =
    task {
        let! actor = caseWorker fixture

        let! opened =
            (Service.execute
                actor
                (OperationInput.Case(
                    request 10 "CONTRACT-001" 0L (AuthoredCaseAct.Open registration)
                ))
                (CancellationToken.None))

        accepted opened |> ignore

        let! closed =
            (Service.execute
                actor
                (OperationInput.Case(request 11 "CONTRACT-001" 1L AuthoredCaseAct.Close))
                (CancellationToken.None))

        let receipt = accepted closed
        Expect.equal (caseReceipt receipt).Revision 2L "One revision"
        Expect.equal (disclosedFields receipt).Status CaseStatus.Closed "Closure stored"
        let! rows = history fixture "CONTRACT-001" 0L 1
        let page = storedPage rows
        Expect.equal page.Entries.Length 1 "Bounded page"
        Expect.equal page.Next (Some 1L) "Continuation"

        let! operation =
            inspectOperation fixture (fixture.Owner) (id 11) (CancellationToken.None)

        match operation with
        | Ok {
                 ExistingOperation = Some entry
                 Case = Some record
             } ->
            Expect.equal entry.Sequence receipt.Position.Sequence "Operation position"
            Expect.equal record.Snapshot.Revision 2L "Current projection"
        | _ -> failtest "Missing accepted update"
    }
