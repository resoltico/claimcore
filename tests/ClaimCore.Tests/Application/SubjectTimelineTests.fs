module ClaimCore.Tests.SubjectTimelineTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport

let private accepted value =
    value |> Result.defaultWith (string >> failtest)

let private caseId =
    CaseFixtures.caseId (Guid.Parse "20000000-0000-4000-8000-000000000001")

let private operation number =
    OperationIds.numberedAt (DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)) number
    |> CaseFixtures.operationId

let private describe content previous =
    TimelineDifferences.describe (DateOnly(2026, 11, 14)) Map.empty content previous

[<Tests>]
let tests =
    testList
        "subject-owned timeline applications"
        [
            testCase
                "work history applies retained values without modern scalar admission"
                (fun () ->
                    let summary = HandlingSummary.parse "Retained\u202esummary" |> accepted

                    Expect.isError
                        (HandlingSummary.admit summary)
                        "The historical value is not currently authorable"

                    let request =
                        {
                            WorkDecisionInput.CaseId = caseId
                            ExpectedRevision = Revision.zero
                            Update =
                                {
                                    Responsible = WorkValue.Keep
                                    Summary = WorkValue.Set summary
                                }
                        }

                    let after =
                        {
                            CaseWorkSnapshot.CaseId = caseId
                            Revision = Revision.first
                            Work =
                                {
                                    Responsible = None
                                    Summary = Some summary
                                }
                        }

                    Expect.equal
                        (describe (EntryContent.Work(request, after)) None)
                        (Ok
                            [
                                TimelineChange.HandlingSummary
                                    { Before = None; After = Some summary }
                            ])
                        "The authored before/after expectation comes from the accepted effect"

                    let forged =
                        { after with
                            Work =
                                { after.Work with
                                    Summary = Some(HandlingSummary.parse "Forged" |> accepted)
                                }
                        }

                    Expect.equal
                        (describe (EntryContent.Work(request, forged)) None)
                        (Error CoreFault.StoreIntegrityError)
                        "Command/effect disagreement remains an integrity failure")
            testCase
                "reminder history applies accepted creation and completion without current eligibility"
                (fun () ->
                    let action = ReminderAction.parse "Retained\u202eaction" |> accepted

                    Expect.isError
                        (ReminderAction.admit action)
                        "The retained action is not a new authoring input"

                    let due = CalendarDate.parse "2026-11-20" |> accepted
                    let id = ReminderId.ofOperationId (operation 1)

                    let before =
                        Reminder.ofAccepted
                            id
                            caseId
                            Revision.first
                            action
                            due
                            ReminderState.Pending
                        |> accepted

                    let create =
                        {
                            ReminderDecisionInput.OperationId = operation 1
                            Command =
                                ReminderCommand.Create(caseId, { Action = action; Due = due })
                        }

                    Expect.equal
                        (describe
                            (EntryContent.Reminder(create, ReminderEffects.Created before))
                            None)
                        (Ok
                            [
                                TimelineChange.Reminder
                                    {
                                        ReminderId = id
                                        Action = Some { Before = None; After = Some action }
                                        Due = Some { Before = None; After = Some due }
                                        State =
                                            Some
                                                {
                                                    Before = None
                                                    After = Some ReminderState.Pending
                                                }
                                    }
                            ])
                        "Retained creation is not re-admitted against current input rules"

                    let after =
                        Reminder.ofAccepted
                            id
                            caseId
                            (CaseFixtures.revision 2L)
                            action
                            due
                            ReminderState.Completed
                        |> accepted

                    let complete =
                        {
                            ReminderDecisionInput.OperationId = operation 2
                            Command =
                                ReminderCommand.Change(
                                    caseId,
                                    id,
                                    Revision.first,
                                    ReminderChange.Complete
                                )
                        }

                    Expect.equal
                        (describe
                            (EntryContent.Reminder(complete, ReminderEffects.Changed after))
                            (Some(EntryContent.Reminder(create, ReminderEffects.Created before))))
                        (Ok
                            [
                                TimelineChange.Reminder
                                    {
                                        ReminderId = id
                                        Action = None
                                        Due = None
                                        State =
                                            Some
                                                {
                                                    Before = Some ReminderState.Pending
                                                    After = Some ReminderState.Completed
                                                }
                                    }
                            ])
                        "Completion changes only the recorded state"

                    let forged =
                        Reminder.ofAccepted
                            id
                            caseId
                            (CaseFixtures.revision 2L)
                            (ReminderAction.parse "Forged" |> accepted)
                            due
                            ReminderState.Completed
                        |> accepted

                    Expect.equal
                        (describe
                            (EntryContent.Reminder(complete, ReminderEffects.Changed forged))
                            (Some(EntryContent.Reminder(create, ReminderEffects.Created before))))
                        (Error CoreFault.StoreIntegrityError)
                        "A state-only command cannot forge different action text")
            testCase "note history uses the subject's accepted add/remove transition" (fun () ->
                let text = NoteText.parse "Retained\u202enote" |> accepted

                Expect.isError
                    (NoteText.admit text)
                    "The retained note is not currently authorable"

                let id = NoteId.ofOperationId (operation 3)
                let note = Note.ofAccepted id caseId (Some text)

                let add =
                    {
                        NoteDecisionInput.OperationId = operation 3
                        Command = NoteCommand.AddNote(caseId, text)
                    }

                Expect.equal
                    (describe (EntryContent.Note(add, note)) None)
                    (Ok [ TimelineChange.Note(NoteActKind.AddNote, id, Some text) ])
                    "Accepted note text remains exact"

                let remove =
                    {
                        NoteDecisionInput.OperationId = operation 4
                        Command = NoteCommand.RemoveNote(caseId, id)
                    }

                let removed = Note.ofAccepted id caseId None

                Expect.equal
                    (describe
                        (EntryContent.Note(remove, removed))
                        (Some(EntryContent.Note(add, note))))
                    (Ok [ TimelineChange.Note(NoteActKind.RemoveNote, id, None) ])
                    "Removal retains identity and clears content"

                let forged = Note.ofAccepted (NoteId.ofOperationId (operation 9)) caseId None

                Expect.equal
                    (describe
                        (EntryContent.Note(remove, forged))
                        (Some(EntryContent.Note(add, note))))
                    (Error CoreFault.StoreIntegrityError)
                    "A removed note cannot change identity")
        ]
