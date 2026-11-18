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
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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

                                Expect.equal tx.Head.Sequence Sequence.zero "Genesis head"
                                Expect.equal tx.Head.EntryHash EntryHash.zero "Zero previous hash"
                                let! person = AggregateStorage.principal tx fixture.Owner
                                Expect.isNone person "No actor before provisioning"

                                return!
                                    Service.provisionOwnerInTransaction
                                        (core fixture fixture.Owner)
                                        tx
                                        (id fixture 1)
                                        DomainInputs.ownerProfile
                                        []
                            })
                    CancellationToken = CancellationToken.None
                }
            )

        match result with
        | Ok(Verdict.Proceed receipt) ->
            Expect.equal receipt.Position.Sequence Sequence.first "First operation sequence"

            Expect.equal
                receipt.RecordedBy
                AuthorityRef.Operator
                "The operator records initialization"
        | _ -> failtest "Expected append"

        Expect.equal calls 1 "One decision"
        let! actor = inspectPeople fixture (fixture.Owner) (CancellationToken.None)

        match actor with
        | Ok({ Actor = Some _ } as people) ->
            Expect.equal people.Installation [ Role.Owner ] "Only the owner role is provisioned"
        | _ -> failtest "Missing owner projection"

        let! observed =
            Service.operationGet
                (core fixture fixture.Owner)
                { OperationId = id fixture 1 }
                CancellationToken.None

        match observed.Outcome with
        | Outcome.Succeeded(OperationLookupResult.Accepted(receipt, false)) ->
            Expect.equal receipt.Position.Sequence Sequence.first "Readable accepting root"
            Expect.equal receipt.RecordedBy AuthorityRef.Operator "The owner did not execute it"
        | other -> failtestf "Missing accepted owner root: %A" other

    }

let internal replayAndRefusal fixture =
    task {
        let! actor = caseWorker fixture

        let original =
            request
                (fixture)
                (10)
                ("CONTRACT-001")
                (0L)
                "case.open"
                (ClaimCore.TestSupport.Submissions.fields (RegistrationInput.values (registration)))

        let! result = (Service.executeSubmission actor ((original)) (CancellationToken.None))

        let receipt = accepted result

        Expect.equal
            receipt.Position.Sequence
            (CaseFixtures.sequence 4L)
            "Bootstrap and two grants precede one case operation"

        let! replay = (Service.executeSubmission actor ((original)) (CancellationToken.None))

        expectReceipt (replayReceipt replay) receipt "Original acceptance with current observation"

        let! stale =
            (Service.executeSubmission
                actor
                (((request (fixture) (11) ("CONTRACT-001") (2L) "case.close" (Map.empty))))
                (CancellationToken.None))

        match stale.Outcome with
        | ExecuteResult.Rejected(Refusal.RevisionConflict conflict) ->
            Expect.equal (Revision.value conflict.ActualRevision) 1L "No stale acceptance"
            Expect.isSome conflict.Changes "The authorized snapshot supplies conflict history"
        | other -> failtestf "Expected revision conflict, got %A" other

        let! view =
            fixture.Store.Read
                {
                    Work = fun tx -> FamilyReads.findCaseByReference tx "CONTRACT-001"
                    CancellationToken = CancellationToken.None
                }

        match view with
        | Ok(Some record) ->
            Expect.equal
                (caseReceipt receipt).Snapshot
                (SnapshotDisclosure.Disclosed
                    {
                        State = LiveCase.ofState record.Snapshot.State |> Option.get
                        Disposition = None
                        RecordedFields =
                            [
                                BusinessField.IncidentDate
                                BusinessField.IncidentNotificationDate
                                BusinessField.IncidentCountry
                                BusinessField.IncidentDescription
                                BusinessField.PolicyNumber
                                BusinessField.InsurerClaimReference
                                BusinessField.ClaimantName
                                BusinessField.InsurerName
                                BusinessField.ClaimedAmount
                                BusinessField.ClaimedCurrency
                            ]
                        Payments = []
                    })
                "Projection and receipt agree"
        | _ -> failtest "Missing case"

        let! rows = caseTimeline fixture "CONTRACT-001" None 50
        Expect.equal (storedPage rows).Entries.Length 1 "Replay/refusal append nothing"
    }

let internal lockedView fixture =
    task {
        let! actor = caseWorker fixture

        let original =
            request
                (fixture)
                (10)
                ("CONTRACT-001")
                (0L)
                "case.open"
                (ClaimCore.TestSupport.Submissions.fields (RegistrationInput.values (registration)))

        let! opened = (Service.executeSubmission actor ((original)) (CancellationToken.None))

        let first = accepted opened
        let mutable calls = 0

        let! result =
            fixture.Store.Write(
                {
                    WriteOperation.Work =
                        (fun tx ->
                            task {
                                calls <- calls + 1

                                Expect.equal
                                    tx.Head.Sequence
                                    first.Position.Sequence
                                    "Current head"

                                Expect.equal
                                    tx.Head.EntryHash
                                    first.Position.EntryHash
                                    "Previous accepted link"

                                let! current = FamilyReads.findCaseByReference tx "CONTRACT-001"

                                Expect.equal
                                    (current |> Option.map _.Snapshot.Revision)
                                    (Some(CaseFixtures.revision 1L))
                                    "Current case"

                                let command = Submissions.command original

                                let context =
                                    {
                                        DecisionContext.BusinessDate = None
                                        ObservedAt = tx.ObservedAt
                                        Principal = actor.Principal
                                        Access = AccessView.empty
                                        Maintenance = false
                                        Lifetime = DecisionLifetime.Execution None
                                    }

                                let! prepared =
                                    Engine.prepare
                                        tx
                                        tx.Head
                                        context
                                        (ClaimCore.Domain.Tasks.Registry.find original.Task
                                         |> Option.get)
                                        (CaseFixtures.operationId original.OperationId)
                                        command.Binding
                                        command.Inputs

                                match prepared with
                                | Error(ExecutionStop.AlreadyAccepted record) ->
                                    Expect.equal
                                        record.Root.Header.OperationId
                                        first.OperationId
                                        "Existing identity in the same locked transaction"

                                    Expect.equal
                                        record.Root.Position
                                        first.Position
                                        "Locked replay proof"

                                    return Verdict.Proceed true
                                | other -> return failtestf "Expected locked replay, got %A" other

                            })
                    CancellationToken = CancellationToken.None
                }
            )

        match result with
        | Ok(Verdict.Proceed true) -> ()
        | _ -> failtest "Expected replay"

        Expect.equal calls 1 "Callback called once"
    }

let internal operatorFamilyRefusal fixture =
    task {
        let! actor = caseWorker fixture

        let submission =
            request
                fixture
                12
                "OPERATOR-FAMILY"
                0L
                "case.open"
                (Submissions.fields (RegistrationInput.values registration))

        let command = Submissions.command submission

        let client, operator =
            match ClaimCore.Domain.Tasks.Registry.find submission.Task with
            | Some(ClaimCore.Domain.Tasks.Task.Family selected as client) ->
                client,
                ClaimCore.Domain.Tasks.Task.Family
                    { selected with
                        Declaration =
                            { selected.Declaration with
                                Exposure = TaskExposure.Operator
                            }
                    }
            | _ -> failtest "Expected a family task"

        let! result =
            fixture.Store.Write
                {
                    Work =
                        fun tx ->
                            task {
                                let context =
                                    {
                                        DecisionContext.BusinessDate =
                                            Some(fixture.Calendar.DateAt tx.ObservedAt)
                                        ObservedAt = tx.ObservedAt
                                        Principal = actor.Principal
                                        Access = AccessView.empty
                                        Maintenance = false
                                        Lifetime = DecisionLifetime.Execution None
                                    }

                                let prepare selected =
                                    Engine.prepare
                                        tx
                                        tx.Head
                                        context
                                        selected
                                        (CaseFixtures.operationId submission.OperationId)
                                        command.Binding
                                        command.Inputs

                                let! control = prepare client
                                Expect.isOk control "The family operation passes actor attribution"

                                let! refused = prepare operator

                                match refused with
                                | Error(ExecutionStop.Failed CoreFault.InternalError) ->
                                    return Verdict.Proceed()
                                | _ ->
                                    return
                                        failtest
                                            "The engine did not reject operator family authority."
                            }
                    CancellationToken = CancellationToken.None
                }

        Expect.equal result (Ok(Verdict.Proceed())) "Neither operation was appended"
    }

let internal caseUpdate fixture =
    task {
        let! actor = caseWorker fixture

        let! opened =
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

        accepted opened |> ignore

        let! closed =
            (Service.executeSubmission
                actor
                (((request (fixture) (11) ("CONTRACT-001") (1L) "case.close" (Map.empty))))
                (CancellationToken.None))

        let receipt = accepted closed
        Expect.equal (caseReceipt receipt).Revision (CaseFixtures.revision (2L)) "One revision"
        Expect.equal (disclosedFields receipt).Status CaseStatus.Closed "Closure stored"
        let! rows = caseTimeline fixture "CONTRACT-001" None 1
        let page = storedPage rows
        Expect.equal page.Entries.Length 1 "Bounded page"

        Expect.equal
            page.Next
            (Some page.Entries.Head.Position)
            "Newest included effect position owns continuation"

        let revisions (entries: StoredEffect list) =
            entries
            |> List.map (fun entry ->
                Expect.equal entry.Change.Kind ClaimCore.Domain.Changes.PartKind.Case "Case part"
                Revision.value entry.Change.Revision)


        Expect.equal (revisions page.Entries) [ 2L ] "First page contains newest revision"
        let! continued = caseTimeline fixture "CONTRACT-001" page.Next 1
        let continued = storedPage continued

        Expect.equal
            (revisions continued.Entries)
            [ 1L ]
            "Continuation returns preceding revision without duplicates"

        Expect.isNone continued.Next "Last page has no continuation"

        let! exhausted =
            caseTimeline fixture "CONTRACT-001" (Some continued.Entries.Head.Position) 1

        Expect.isEmpty (storedPage exhausted).Entries "Continuation below opening revision is empty"

        let! operation =
            fixture.Store.Read
                {
                    Work =
                        fun tx ->
                            task {
                                let! entry = LedgerReads.findOperation tx receipt.OperationId
                                let! record = FamilyReads.findCaseByReference tx "CONTRACT-001"
                                return entry, record
                            }
                    CancellationToken = CancellationToken.None
                }

        match operation with
        | Ok(Some entry, Some record) ->
            Expect.equal entry.Root.Position.Sequence receipt.Position.Sequence "Operation position"
            Expect.equal record.Snapshot.Revision (CaseFixtures.revision 2L) "Current projection"
        | _ -> failtest "Missing accepted update"
    }
