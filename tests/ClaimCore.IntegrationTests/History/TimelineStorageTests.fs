module ClaimCore.IntegrationTests.TimelineStorageTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.ApplicationFixtures

let private token = CancellationToken.None

let private required =
    function
    | Ok value -> value
    | Error problem -> failtestf "Unexpected timeline fault: %A" problem

[<Tests>]
let tests =
    testCaseAsync
        "timeline storage orders mixed effects and finds next-reminder predecessors"
        (run (fun _ fixture ->
            task {
                let! owner = caseWorker fixture

                for number, revision, taskName, values in
                    [
                        99100,
                        0L,
                        "case.open",
                        Submissions.fields (RegistrationInput.values registration)
                        99101, 1L, "case.close", CaseTransitionInputs.closed
                        99102, 2L, "case.reopen", CaseTransitionInputs.reopened
                    ] do
                    let! reply =
                        Service.executeSubmission
                            owner
                            (request fixture number "TIMELINE-1" revision taskName values)
                            token

                    accepted reply |> ignore

                let! work =
                    Service.executeSubmission
                        owner
                        ((ClaimCore.TestSupport.Submissions.createRaw)
                            "work.update"
                            (id fixture 99103)
                            (ClaimCore.Application.Contracts.OperationBinding.Work(
                                (caseTarget fixture "TIMELINE-1"),
                                (0L)
                            ))
                            (ClaimCore.TestSupport.Submissions.work (
                                {
                                    Responsible = WorkValue.Keep
                                    Summary = WorkValue.Set "Timeline summary"
                                }
                            )))
                        token

                accepted work |> ignore

                for number, taskName, selected, values in
                    [
                        99104,
                        "reminder.create",
                        None,
                        Submissions.reminderPlan "First action" "2027-12-30"
                        99105,
                        "reminder.completeAndPlanNext",
                        Some(id fixture 99104, 1L),
                        Submissions.reminderPlan "Next action" "2027-12-31"
                        99106,
                        "reminder.update",
                        Some(id fixture 99105, 1L),
                        Submissions.reminderPlan "Updated next action" "2027-12-31"
                    ] do
                    let! reply =
                        Service.executeSubmission
                            owner
                            (Submissions.createRaw
                                taskName
                                (id fixture number)
                                (OperationBinding.Reminder(
                                    caseTarget fixture "TIMELINE-1",
                                    selected
                                ))
                                values)
                            token

                    accepted reply |> ignore

                let! result =
                    owner.Store.Read
                        {
                            CancellationToken = token
                            Work =
                                fun tx ->
                                    task {
                                        let! parent =
                                            FamilyReads.findCase
                                                tx
                                                (CaseFixtures.caseId (id fixture 99100))

                                        let parent =
                                            parent
                                            |> Option.defaultWith (fun () ->
                                                failtest "Created parent must exist")

                                        let query: TimelineStorage.PageQuery =
                                            {
                                                TimelineStorage.PageQuery.CaseId = parent.CaseId
                                                Filter = None
                                                Range = None
                                                BeforePosition = None
                                                Limit = 50
                                            }

                                        let! all = LedgerReads.timelinePage tx query
                                        let! previous = LedgerReads.previousEntries tx all.Items

                                        let! facts =
                                            LedgerReads.timelinePage
                                                tx
                                                { query with
                                                    Filter = Some TimelineFilter.Facts
                                                }

                                        let! work =
                                            LedgerReads.timelinePage
                                                tx
                                                { query with
                                                    Filter = Some TimelineFilter.Work
                                                }

                                        let! first =
                                            LedgerReads.timelinePage tx { query with Limit = 1 }

                                        let! firstPrevious =
                                            LedgerReads.previousEntries tx first.Items

                                        Expect.equal
                                            firstPrevious.Count
                                            1
                                            "A one-row page still loads its predecessor outside the visible page"

                                        Expect.equal
                                            (firstPrevious
                                             |> Map.find first.Items.Head.Position
                                             |> fun entry ->
                                                 OperationId.value entry.Stamp.OperationId)
                                            (id fixture 99105)
                                            "Paging does not truncate next-reminder creation context"

                                        let! rest =
                                            LedgerReads.timelinePage
                                                tx
                                                { query with
                                                    BeforePosition = first.Next
                                                }

                                        let! described =
                                            TimelineHistory.entries
                                                owner
                                                tx
                                                parent.Snapshot.Reference
                                                false
                                                true
                                                None
                                                all

                                        let differences =
                                            described
                                            |> required
                                            |> List.map (fun entry ->
                                                let values =
                                                    match entry.Changes with
                                                    | TimelineChanges.Disclosed value ->
                                                        value.Items
                                                    | _ -> failtest "Full history required"

                                                EffectId.create
                                                    entry.OperationId
                                                    (EffectPosition.index entry.Position)
                                                |> Option.get,
                                                values)
                                            |> Map.ofList

                                        Expect.isError
                                            (PartDescriptions.familyVersions
                                                parent.Snapshot.Reference
                                                Map.empty
                                                all.Items.Head.Operation.Changes)
                                            "A missing predecessor cannot turn a revision-two update into creation"

                                        return all, previous, facts, work, first, rest, differences
                                    }
                        }

                match result with
                | Error error -> failtestf "Timeline storage failed: %A" error
                | Ok(all, previous, facts, work, first, rest, differences) ->
                    Expect.equal
                        (all.Items |> List.map (fun entry -> OperationId.value entry.OperationId))
                        ([ 99106; 99105; 99105; 99104; 99103; 99102; 99101; 99100 ]
                         |> List.map (id fixture))
                        "Ordering follows shared ledger sequence across independent revisions"

                    Expect.equal facts.Items.Length 3 "Facts excludes work and reminders"
                    Expect.equal work.Items.Length 5 "Work includes work and reminders"

                    Expect.equal
                        (first.Items @ rest.Items |> List.map _.Position)
                        (all.Items |> List.map _.Position)
                        "The cursor continues strictly after the visible entry"

                    Expect.equal
                        previous.Count
                        4
                        "Only revisions with actual predecessors are returned"

                    let changes number =
                        differences
                        |> Map.toList
                        |> List.filter (fun (effect, _) ->
                            EffectId.operation effect =
                                CaseFixtures.operationId (id fixture number))
                        |> List.collect snd

                    Expect.equal
                        (changes 99102)
                        [
                            TimelineChange.CaseField
                                {
                                    FieldName = BusinessField.Outcome
                                    Before =
                                        Some(
                                            CaseFieldValue.Business(
                                                Admitted.Outcome ClosureOutcome.Other
                                            )
                                        )
                                    After = None
                                }
                            TimelineChange.CaseField
                                {
                                    FieldName = BusinessField.Status
                                    Before = Some(CaseFieldValue.Status CaseStatus.Closed)
                                    After = Some(CaseFieldValue.Status CaseStatus.Opened)
                                }
                            TimelineChange.CaseField
                                {
                                    FieldName = BusinessField.ReopenReason
                                    Before = None
                                    After =
                                        Reason.parse "Synthetic work resumed"
                                        |> Result.defaultWith (string >> failtest)
                                        |> Admitted.Reason
                                        |> CaseFieldValue.Business
                                        |> Some
                                }
                        ]
                        "Reopening compares the preceding outcome and reports the authored reason"

                    match changes 99103 with
                    | [ TimelineChange.HandlingSummary change ] ->
                        Expect.isNone change.Before "First work revision starts from empty work"

                        Expect.equal
                            (change.After |> Option.map HandlingSummary.format)
                            (Some "Timeline summary")
                            "The work difference retains typed text"
                    | other -> failtestf "Unexpected work changes: %A" other

                    match changes 99106 with
                    | [ TimelineChange.Reminder change ] ->
                        Expect.equal
                            (ReminderId.value change.ReminderId)
                            (id fixture 99105)
                            "The change belongs to the new reminder"

                        Expect.equal
                            (change.Action
                             |> Option.bind _.Before
                             |> Option.map ReminderAction.format)
                            (Some "Next action")
                            "Comparison starts from the embedded creation snapshot"

                        Expect.equal
                            (change.Action
                             |> Option.bind _.After
                             |> Option.map ReminderAction.format)
                            (Some "Updated next action")
                            "The new action is distinct"

                        Expect.isNone change.Due "Unchanged dates are omitted"
                        Expect.isNone change.State "Unchanged state is omitted"
                    | other -> failtestf "Unexpected next-reminder changes: %A" other

                    Expect.equal
                        (changes 99105 |> List.length)
                        2
                        "Complete-and-plan-next describes both identities"

                    let updated = List.head all.Items
                    let origin = Map.find updated.Position previous

                    Expect.equal
                        (OperationId.value origin.Stamp.OperationId)
                        (id fixture 99105)
                        "A reminder's predecessor can be its complete-and-plan-next creation effect"

                    Expect.equal origin.Kind PartKind.Reminder "Reminder predecessor"

                    Expect.equal
                        origin.PartId
                        (id fixture 99105)
                        "Next reminder owns its creation part"

                    Expect.equal
                        (Revision.value origin.Revision)
                        1L
                        "Creation part is first revision"

                    Expect.equal
                        (EffectPosition.index origin.Stamp.Position)
                        1
                        "The second part of the paired operation owns the next reminder"
            }))
