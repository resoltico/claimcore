module ClaimCore.Tests.WorkProjectionTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts

let private required =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

let private parent =
    Guid.Parse "95000000-0000-4000-8000-000000000001" |> CaseId.create |> Option.get

let private actor =
    Guid.Parse "95000000-0000-4000-8000-000000000002"
    |> ActorId.create
    |> Option.get

let private instant = DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero)

let private workSnapshot =
    {
        CaseWorkSnapshot.CaseId = parent
        Revision = Revision.first
        Work =
            {
                Responsible = Some actor
                Summary = HandlingSummary.parse "Independently expected summary" |> required |> Some
            }
    }

let private workRow: WorkSubjectRows =
    {
        CaseId = parent
        Revision = Revision.first
        Snapshot = Some workSnapshot
        Responsible = Some actor
        Summary = HandlingSummary.parse "Independently expected summary" |> required |> Some
        SearchText = Some "independently expected summary"
        ChangedAt = Some instant
        RecordedBy = Some actor
        SummaryChangedAt = Some instant
        SummaryRecordedBy = Some actor
    }

let private reminderId =
    Guid.Parse "95000000-0000-4000-8000-000000000003"
    |> ReminderId.create
    |> Option.get

let private reminder =
    Reminder.ofAccepted
        reminderId
        parent
        Revision.first
        (ReminderAction.parse "Inspect evidence" |> required)
        (CalendarDate.parse "2026-11-10" |> required)
        ReminderState.Pending
    |> required

let private reminderRow: ReminderSubjectRows =
    {
        ReminderId = reminderId
        CaseId = parent
        Revision = Revision.first
        Snapshot = Some reminder
        Status = Some ReminderState.Pending
        Action = ReminderAction.parse "Inspect evidence" |> required |> Some
        Due = CalendarDate.parse "2026-11-10" |> required |> Some
        SearchText = Some "inspect evidence"
        RecordedAt = instant
        RecordedBy = actor
    }

let private workRows expected actual =
    ProjectionVerification.rows
        VerificationFinding.CaseWorkProjection
        (fun (row: WorkSubjectRows) -> row.CaseId)
        expected
        actual

let private reminderRows expected actual =
    ProjectionVerification.rows
        VerificationFinding.CaseWorkProjection
        (fun (row: ReminderSubjectRows) -> row.ReminderId)
        expected
        actual

[<Tests>]
let tests =
    testList
        "independent work projection controls"
        [
            testCase
                "work rows require exact membership content revision and attribution"
                (fun () ->
                    let expected = Map.ofList [ (parent, workRow) ]
                    Expect.isOk (workRows expected [ workRow ]) "Positive root"

                    for rows in
                        [
                            []
                            [ workRow; workRow ]
                            [
                                { workRow with
                                    Revision = Revision.zero
                                }
                            ]
                            [ { workRow with Snapshot = None } ]
                            [
                                { workRow with
                                    ChangedAt = Some(instant.AddSeconds 1.)
                                }
                            ]
                            [ { workRow with RecordedBy = None } ]
                            [
                                { workRow with
                                    SummaryChangedAt = Some(instant.AddSeconds 1.)
                                }
                            ]
                            [
                                { workRow with
                                    SummaryRecordedBy = None
                                }
                            ]
                        ] do
                        Expect.isError (workRows expected rows) "Independent corruption detected"

                    let erased =
                        { workRow with
                            Snapshot = None
                            Responsible = None
                            Summary = None
                            SearchText = None
                            SummaryChangedAt = None
                            SummaryRecordedBy = None
                        }

                    let tombstone =
                        { workRow with
                            Snapshot = None
                            Responsible = None
                            Summary = None
                            SearchText = None
                            SummaryChangedAt = None
                            SummaryRecordedBy = None
                        }

                    Expect.isOk
                        (workRows (Map.ofList [ (parent, erased) ]) [ tombstone ])
                        "Erased identity remains structural"

                    Expect.isError
                        (workRows (Map.ofList [ (parent, erased) ]) [ workRow ])
                        "Payload resurrection refuses")
            testCase
                "reminder rows require exact parent identity version content and attribution"
                (fun () ->
                    let expected = Map.ofList [ (reminderId, reminderRow) ]

                    Expect.isOk (reminderRows expected [ reminderRow ]) "Positive current reminder"

                    let foreign =
                        Guid.Parse "95000000-0000-4000-8000-000000000004"
                        |> CaseId.create
                        |> Option.get

                    for rows in
                        [
                            []
                            [ reminderRow; reminderRow ]
                            [ { reminderRow with CaseId = foreign } ]
                            [
                                { reminderRow with
                                    Revision = Revision.create 2L |> Option.get
                                }
                            ]
                            [ { reminderRow with Snapshot = None } ]
                            [
                                { reminderRow with
                                    RecordedAt = instant.AddSeconds 1.
                                }
                            ]
                        ] do
                        Expect.isError
                            (reminderRows expected rows)
                            "Cross-entity corruption detected"

                    let erased =
                        { reminderRow with
                            Snapshot = None
                            Status = None
                            Action = None
                            Due = None
                            SearchText = None
                        }

                    let tombstone =
                        { reminderRow with
                            Snapshot = None
                            Status = None
                            Action = None
                            Due = None
                            SearchText = None
                        }

                    Expect.isOk
                        (reminderRows (Map.ofList [ (reminderId, erased) ]) [ tombstone ])
                        "Erased ID and causal revision remain reserved"

                    Expect.isError
                        (reminderRows (Map.ofList [ (reminderId, erased) ]) [ reminderRow ])
                        "Erased payload cannot reappear")
        ]
