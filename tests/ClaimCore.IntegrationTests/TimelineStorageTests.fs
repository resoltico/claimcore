module ClaimCore.IntegrationTests.TimelineStorageTests

open System.Threading
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
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

                for number, revision, act in
                    [
                        99100, 0L, AuthoredCaseAct.Open registration
                        99101, 1L, AuthoredCaseAct.Bare BareAct.Close
                        99102, 2L, AuthoredCaseAct.Bare BareAct.Reopen
                    ] do
                    let! reply =
                        Service.execute
                            owner
                            (Inputs.caseOperation (
                                request fixture number "TIMELINE-1" revision act
                            ))
                            token

                    accepted reply |> ignore

                let! work =
                    Service.execute
                        owner
                        (OperationInput.Work
                            {
                                OperationId = id fixture 99103
                                Target = CaseSelector.Reference "TIMELINE-1"
                                ExpectedRevision = 0L
                                Act =
                                    WorkAct.UpdateWork
                                        {
                                            Responsible = WorkValue.Keep
                                            Summary = WorkValue.Set "Timeline summary"
                                        }
                            })
                        token

                accepted work |> ignore

                for number, act in
                    [
                        99104,
                        AuthoredReminderAct.Create
                            {
                                Action = "First action"
                                Due = "2027-12-30"
                            }
                        99105,
                        AuthoredReminderAct.CompleteAndPlanNext(
                            id fixture 99104,
                            1L,
                            {
                                Action = "Next action"
                                Due = "2027-12-31"
                            }
                        )
                        99106,
                        AuthoredReminderAct.Update(
                            id fixture 99105,
                            1L,
                            {
                                Action = "Updated next action"
                                Due = "2027-12-31"
                            }
                        )
                    ] do
                    let! reply =
                        Service.execute
                            owner
                            (OperationInput.Reminder
                                {
                                    OperationId = id fixture number
                                    Target = CaseSelector.Reference "TIMELINE-1"
                                    Act = act
                                })
                            token

                    accepted reply |> ignore

                let! result =
                    owner.Store.Read
                        {
                            CancellationToken = token
                            Work =
                                fun tx ->
                                    task {
                                        let! parent = tx.FindCaseByReference "TIMELINE-1"

                                        let parent =
                                            parent
                                            |> Option.defaultWith (fun () ->
                                                failtest "Created parent must exist")

                                        let query =
                                            {
                                                TimelinePageQuery.CaseId = parent.CaseId
                                                Filter = None
                                                Range = None
                                                BeforePosition = None
                                                Limit = 50
                                            }

                                        let! all = tx.TimelinePage query
                                        let! previous = tx.PreviousEntries all.Items

                                        let! facts =
                                            tx.TimelinePage
                                                { query with
                                                    Filter = Some TimelineFilter.Facts
                                                }

                                        let! work =
                                            tx.TimelinePage
                                                { query with
                                                    Filter = Some TimelineFilter.Work
                                                }

                                        let! first = tx.TimelinePage { query with Limit = 1 }
                                        let! firstPrevious = tx.PreviousEntries first.Items

                                        Expect.equal
                                            firstPrevious.Count
                                            1
                                            "A one-row page still loads its predecessor outside the visible page"

                                        Expect.equal
                                            (firstPrevious
                                             |> Map.find first.Items.Head.Position
                                             |> fun entry -> OperationId.value entry.OperationId)
                                            (id fixture 99105)
                                            "Paging does not truncate next-reminder creation context"

                                        let! rest =
                                            tx.TimelinePage
                                                { query with
                                                    BeforePosition = first.Next
                                                }

                                        let content entry =
                                            LedgerEntries.validate owner.InstallationId entry
                                            |> required
                                            |> LedgerEntries.content

                                        let! actors =
                                            tx.ActorRefs(
                                                all.Items |> List.map _.ActorId |> List.distinct
                                            )

                                        let differences =
                                            all.Items
                                            |> List.map (fun entry ->
                                                let before =
                                                    previous
                                                    |> Map.tryFind entry.Position
                                                    |> Option.map content

                                                entry.Id,
                                                (TimelineDifferences.describeEffect
                                                    entry.Subject
                                                    (match entry.Subject with
                                                     | EntryTarget.Case(_, _, _, date) -> date
                                                     | EntryTarget.Work value ->
                                                         value.BusinessDate
                                                     | EntryTarget.Reminder value ->
                                                         value.BusinessDate
                                                     | _ -> failtest "Case timeline entry")
                                                    actors
                                                    (content entry)
                                                    before
                                                 |> required))
                                            |> Map.ofList

                                        Expect.isError
                                            (TimelineDifferences.describe
                                                (System.DateOnly(2026, 9, 7))
                                                actors
                                                (content all.Items.Head)
                                                None)
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
                                    FieldName = BusinessField.Status
                                    Before = Some(CaseFieldValue.Status CaseStatus.Closed)
                                    After = Some(CaseFieldValue.Status CaseStatus.Opened)
                                }
                        ]
                        "Case status differences come from the immediate case predecessor"

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
                        (OperationId.value origin.OperationId)
                        (id fixture 99105)
                        "A reminder's predecessor can be its complete-and-plan-next creation effect"

                    match origin.Subject with
                    | EntryTarget.Reminder subject ->
                        Expect.equal
                            subject.ReminderId
                            (ReminderId.create (id fixture 99105) |> Option.get)
                            "The predecessor is the next reminder's own creation effect"

                        Expect.equal
                            subject.Act
                            ReminderActKind.Create
                            "Birth is distinct from completing the original reminder"

                        Expect.equal
                            origin.Index
                            1
                            "The second effect of the paired operation owns the new reminder"
                    | _ -> failtest "Expected a reminder birth effect"

            }))
