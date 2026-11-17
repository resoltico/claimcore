module ClaimCore.Tests.SubjectTimelineTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.TestSupport

let private accepted value =
    value |> Result.defaultWith (string >> failtest)

let private id = Guid.Parse "20000000-0000-4000-8000-000000000001"

let private describe description =
    PartDescriptions.timeline
        Map.empty
        {
            PartId = id
            Revision = Revision.first
            Description = description
        }
    |> accepted
    |> _.Items

[<Tests>]
let tests =
    testList
        "accepted part timeline values"
        [
            testCase "work descriptions retain text without modern admission" (fun () ->
                let summary = HandlingSummary.parse "Retained\u202esummary" |> accepted
                Expect.isError (HandlingSummary.admit summary) "Negative authoring control"

                let after =
                    {
                        CaseWork.Responsible = None
                        Summary = Some summary
                    }

                Expect.equal
                    (WorkDescription.describe None (Some after) |> PartDescription.Work |> describe)
                    [ TimelineChange.HandlingSummary { Before = None; After = Some summary } ]
                    "Exact accepted text")
            testCase "reminder creation and completion preserve the retained plan" (fun () ->
                let action = ReminderAction.parse "Retained\u202eaction" |> accepted
                Expect.isError (ReminderAction.admit action) "Negative authoring control"
                let due = CalendarDate.parse "2026-11-20" |> accepted

                let before =
                    {
                        ReminderValue.Plan = Some { Action = action; Due = due }
                        State = ReminderState.Pending
                    }

                let reminderId = ReminderId.create id |> Option.get

                Expect.equal
                    (ReminderDescription.describe None before
                     |> PartDescription.Reminder
                     |> describe)
                    [
                        TimelineChange.Reminder
                            {
                                ReminderId = reminderId
                                Action = Some { Before = None; After = Some action }
                                Due = Some { Before = None; After = Some due }
                                State =
                                    Some
                                        {
                                            Before = None
                                            After = Some ReminderState.Pending
                                        }
                            }
                    ]
                    "Accepted creation"

                let after =
                    { before with
                        State = ReminderState.Completed
                    }

                Expect.equal
                    (ReminderDescription.describe (Some before) after
                     |> PartDescription.Reminder
                     |> describe)
                    [
                        TimelineChange.Reminder
                            {
                                ReminderId = reminderId
                                Action = None
                                Due = None
                                State =
                                    Some
                                        {
                                            Before = Some ReminderState.Pending
                                            After = Some ReminderState.Completed
                                        }
                            }
                    ]
                    "Completion changes only state")
            testCase
                "note descriptions preserve accepted add and removal identity and text"
                (fun () ->
                    let text = NoteText.parse "Retained\u202enote" |> accepted
                    Expect.isError (NoteText.admit text) "Negative authoring control"

                    let before =
                        {
                            NoteValue.Text = Some text
                            Removed = false
                        }

                    let noteId = NoteId.create id |> Option.get

                    Expect.equal
                        (NoteDescription.describe None before |> PartDescription.Note |> describe)
                        [ TimelineChange.Note(false, noteId, Some text) ]
                        "Exact added text"

                    let after =
                        {
                            NoteValue.Text = None
                            Removed = true
                        }

                    Expect.equal
                        (NoteDescription.describe (Some before) after
                         |> PartDescription.Note
                         |> describe)
                        [ TimelineChange.Note(true, noteId, Some text) ]
                        "Removal description retains preceding accepted text")
        ]
