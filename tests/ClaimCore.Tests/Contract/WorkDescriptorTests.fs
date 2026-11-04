module ClaimCore.Tests.WorkDescriptorTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Protocol.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private basis =
    {
        ObservedAt = DateTimeOffset.Parse "2026-11-04T12:00:00Z"
        BusinessDate = DateOnly(2026, 11, 4)
        TimeZoneId = "Europe/Riga"
    }

[<Tests>]
let tests =
    testList
        "typed work descriptors"
        [
            testCase "overview preserves independent counts and a known zero" (fun () ->
                let reply =
                    {
                        Basis = basis
                        Mine = false
                        ActionableCases = 11L
                        MissingPlanCases = 0L
                        OverdueCases = 3L
                        OverdueReminders = 4L
                        UnassignedCases = 5L
                        UnavailableAssigneeCases = 6L
                        AttentionCases = 7L
                    }

                let entries = Descriptor.workOverview reply

                Expect.equal
                    (entries |> List.map (fun entry -> entry.Key, entry.Value))
                    [
                        "actionableCases", PresentedValue.Count 11L
                        "missingPlanCases", PresentedValue.Count 0L
                        "overdueCases", PresentedValue.Count 3L
                        "overdueReminders", PresentedValue.Count 4L
                        "unassignedCases", PresentedValue.Count 5L
                        "unavailableAssigneeCases", PresentedValue.Count 6L
                        "attentionCases", PresentedValue.Count 7L
                    ]
                    "The core determines count identity, order and value without client recomputation"

                Expect.contains
                    (TextWorkReply.overview "en" reply)
                    "Cases without next action: 0"
                    "The actual human renderer retains zero rather than an absent marker")
            testCase "view counts retain their typed identities and producer order" (fun () ->
                let cases: CaseViews =
                    {
                        Basis = basis
                        Items =
                            [
                                { View = CaseView.Mine; Count = 0L }
                                { View = CaseView.All; Count = 9L }
                            ]
                    }

                let reminders: ReminderViews =
                    {
                        Basis = basis
                        Items =
                            [
                                {
                                    View = ReminderView.Cancelled
                                    Count = 2L
                                }
                            ]
                    }

                Expect.equal
                    (Descriptor.caseViews cases
                     |> List.map (fun entry -> entry.LabelId, entry.Value))
                    [
                        "view.MINE.label", PresentedValue.Count 0L
                        "view.ALL.label", PresentedValue.Count 9L
                    ]
                    "Case view labels and counts come from typed core values"

                Expect.equal
                    (Descriptor.reminderViews reminders
                     |> List.map (fun entry -> entry.LabelId, entry.Value))
                    [ "view.CANCELLED.label", PresentedValue.Count 2L ]
                    "Reminder views do not borrow case selection rules")
            testCase "full case facts retain non-listed fields and pair money once" (fun () ->
                let reference = Inputs.reference "CARD-1"

                let facts: CaseFields =
                    {
                        CaseReference = reference
                        Status = CaseStatus.Opened
                        Registration = Inputs.admittedRegistration Inputs.registration
                        Progress = PaymentProgress.Undecided
                    }

                let snapshot =
                    {
                        CaseId =
                            CaseFixtures.caseId (Guid.Parse "80000000-0000-4000-8000-000000000001")
                        Reference = reference
                        Revision = CaseFixtures.revision 1L
                        State = CaseState.Active facts
                    }

                let sections = Descriptor.caseFacts snapshot
                let entries = sections |> List.collect _.Entries

                Expect.equal
                    (sections |> List.map _.TitleId)
                    [ "group.registration.label"; "group.decision.label"; "group.payment.label" ]
                    "Facts keep the core group order"

                Expect.isTrue
                    (entries |> List.exists (fun entry -> entry.Key = "incidentDate"))
                    "Full cards retain facts omitted from list rows"

                Expect.equal
                    (entries
                     |> List.filter (fun entry ->
                         match entry.Value with
                         | PresentedValue.Money _ -> true
                         | _ -> false)
                     |> List.length)
                    1
                    "Claimed money is one typed value and the absent decision stays absent"

                Expect.isFalse
                    (entries |> List.exists (fun entry -> entry.Key = "claimedCurrency"))
                    "The currency is not duplicated")
            testCase "erased case cards suppress leftover work and facts" (fun () ->
                let state = CaseState.Erased

                let reply: CaseReadModel =
                    {
                        Snapshot =
                            {
                                CaseId =
                                    CaseFixtures.caseId (
                                        Guid.Parse "80000000-0000-4000-8000-000000000002"
                                    )
                                Reference = Inputs.reference "ERASED-1"
                                Revision = CaseFixtures.revision 2L
                                State = state
                            }
                        Work =
                            { Inputs.workView state with
                                Summary = HandlingSummary.parse "SECRET" |> Result.toOption
                            }
                        Advice = Inputs.caseAdvice state
                        Mine = false
                        Headline = CaseHeadline.Erased
                        Attention = []
                        Stages = CaseStageDisplay.Erased
                    }

                let sections = Descriptor.caseView reply
                Expect.equal sections.Length 1 "Only the identifying header remains"

                Expect.equal
                    (sections.Head.Entries |> List.map _.Key)
                    [ "caseReference"; "headline"; "revision" ]
                    "No work or financial value can be rendered"

                Expect.isFalse
                    (TextCaseReply.caseLines "en" reply
                     |> List.exists (fun line -> line.Contains "SECRET"))
                    "Actual human output cannot disclose stale work")
            testCase "reminder descriptor preserves actor and due date as typed values" (fun () ->
                let actorId =
                    CaseFixtures.actorId (Guid.Parse "80000000-0000-4000-8000-000000000003")

                let actor = Inputs.namedActorReference actorId "Aija"
                let due = Inputs.date "2026-11-05"

                let reminder =
                    Reminder.ofAccepted
                        (ReminderId.ofOperationId (
                            CaseFixtures.operationId (
                                Guid.Parse "80000000-0000-4000-8000-000000000004"
                            )
                        ))
                        (CaseFixtures.caseId (Guid.Parse "80000000-0000-4000-8000-000000000005"))
                        (CaseFixtures.revision 1L)
                        (ReminderAction.parse "Telephone claimant"
                         |> Result.defaultWith (fun error -> failtestf "%A" error))
                        due
                        ReminderState.Pending
                    |> Result.defaultWith failtest

                let reply: ReminderReadModel =
                    {
                        Reminder = reminder
                        Reference = Inputs.reference "REM-1"
                        RecordedAt = basis.ObservedAt
                        RecordedBy = actor
                        Acts = [ ReminderActKind.Complete ]
                        Active = true
                        Timing = Some ReminderDue.Upcoming
                    }

                let entries = Descriptor.reminder reply |> List.collect _.Entries

                let field key =
                    entries |> List.find (fun entry -> entry.Key = key) |> _.Value

                Expect.equal
                    (field "dueDate")
                    (PresentedValue.Date due)
                    "The CLI need not parse a date from arbitrary text"

                Expect.equal
                    (field "recordedBy")
                    (PresentedValue.Actor actor)
                    "Names retain the core ActorRef"

                Expect.equal
                    (field "timing")
                    (PresentedValue.Code("reminderDue", "UPCOMING"))
                    "Timing is core-owned"

                Expect.isFalse
                    (entries |> List.exists (fun entry -> entry.Key = "parentState"))
                    "An active parent has no invented inactivity row")
        ]
