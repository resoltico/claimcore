module ClaimCore.Tests.WorkProjectionTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat

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

let private work: WorkHistory =
    {
        Revision = Revision.first
        Snapshot = Some workSnapshot
        ChangedAt = Some instant
        RecordedBy = Some actor
        SummaryChangedAt = Some instant
        SummaryRecordedBy = Some actor
        MissingPayloadAt = None
        PresentPayloadAt = Some Sequence.first
    }

let private workRow: StoredWork =
    {
        CaseId = parent
        Revision = Revision.first
        Snapshot = Some workSnapshot
        SnapshotBytes = WorkRecord.encodeSnapshot workSnapshot
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

let private reminderHistory: ReminderHistory =
    {
        CaseId = parent
        Revision = Revision.first
        State = ReminderState.Pending
        Snapshot = Some reminder
        RecordedAt = instant
        RecordedBy = actor
        MissingPayloadAt = None
        PresentPayloadAt = Some Sequence.first
    }

let private reminderRow: StoredReminder =
    {
        ReminderId = reminderId
        CaseId = parent
        Revision = Revision.first
        Snapshot = Some reminder
        SnapshotBytes = ReminderRecord.encodeSnapshot reminder
        RecordedAt = instant
        RecordedBy = actor
    }

[<Tests>]
let tests =
    testList
        "independent work projection controls"
        [
            testCase "work rows require exact membership bytes revision and attribution" (fun () ->
                let expected = Map.ofList [ (parent, work) ]
                Expect.isOk (ProjectionVerification.workRows expected [ workRow ]) "Positive root"

                for rows in
                    [
                        []
                        [ workRow; workRow ]
                        [
                            { workRow with
                                Revision = Revision.zero
                            }
                        ]
                        [
                            { workRow with
                                SnapshotBytes = [| 0uy |]
                            }
                        ]
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
                    Expect.isError
                        (ProjectionVerification.workRows expected rows)
                        "Independent corruption detected"

                let erased =
                    { work with
                        Snapshot = None
                        SummaryChangedAt = None
                        SummaryRecordedBy = None
                    }

                let tombstone =
                    { workRow with
                        Snapshot = None
                        SnapshotBytes = Array.empty
                        SummaryChangedAt = None
                        SummaryRecordedBy = None
                    }

                Expect.isOk
                    (ProjectionVerification.workRows
                        (Map.ofList [ (parent, erased) ])
                        [ tombstone ])
                    "Erased identity remains structural"

                Expect.isError
                    (ProjectionVerification.workRows (Map.ofList [ (parent, erased) ]) [ workRow ])
                    "Payload resurrection refuses")
            testCase
                "reminder rows require exact parent identity version bytes and attribution"
                (fun () ->
                    let expected = Map.ofList [ (reminderId, reminderHistory) ]

                    Expect.isOk
                        (ProjectionVerification.reminderRows expected [ reminderRow ])
                        "Positive current reminder"

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
                            [
                                { reminderRow with
                                    SnapshotBytes = [| 0uy |]
                                }
                            ]
                            [
                                { reminderRow with
                                    RecordedAt = instant.AddSeconds 1.
                                }
                            ]
                        ] do
                        Expect.isError
                            (ProjectionVerification.reminderRows expected rows)
                            "Cross-entity corruption detected"

                    let erased = { reminderHistory with Snapshot = None }

                    let tombstone =
                        { reminderRow with
                            Snapshot = None
                            SnapshotBytes = Array.empty
                        }

                    Expect.isOk
                        (ProjectionVerification.reminderRows
                            (Map.ofList [ (reminderId, erased) ])
                            [ tombstone ])
                        "Erased ID and causal revision remain reserved"

                    Expect.isError
                        (ProjectionVerification.reminderRows
                            (Map.ofList [ (reminderId, erased) ])
                            [ reminderRow ])
                        "Erased payload cannot reappear")
        ]
