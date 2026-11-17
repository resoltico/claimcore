module ClaimCore.Tests.WorkProjectionTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
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

let private workRows expected actual =
    ProjectionVerification.rows
        VerificationFinding.CaseWorkProjection
        (fun (row: StoredWork) -> row.CaseId)
        expected
        actual

let private reminderRows expected actual =
    ProjectionVerification.rows
        VerificationFinding.CaseWorkProjection
        (fun (row: StoredReminder) -> row.ReminderId)
        expected
        actual

let private fromColumns read columns =
    use table = new System.Data.DataTable()

    for name, _ in columns do
        table.Columns.Add(name, typeof<obj>) |> ignore

    table.Rows.Add(columns |> List.map snd |> List.toArray) |> ignore
    use reader = table.CreateDataReader()
    reader.Read() |> ignore
    DatabaseSession.namedRow reader () |> read

let private workColumns =
    [
        "case_id", box (CaseId.value parent)
        "revision", box 1L
        "snapshot", box (WorkRecord.encodeSnapshot workSnapshot)
        "responsible_actor", box (ActorId.value actor)
        "handling_summary", box "Independently expected summary"
        "search_text", box "independently expected summary"
        "changed_at", box "2026-11-02T12:00:00.000000Z AD"
        "recorded_by", box (ActorId.value actor)
        "summary_changed_at", box "2026-11-02T12:00:00.000000Z AD"
        "summary_recorded_by", box (ActorId.value actor)
    ]

let private reminderColumns =
    [
        "reminder_id", box (ReminderId.value reminderId)
        "case_id", box (CaseId.value parent)
        "revision", box 1L
        "snapshot", box (ReminderRecord.encodeSnapshot reminder)
        "state", box "PENDING"
        "action", box "Inspect evidence"
        "due", box "2026-11-10 AD"
        "search_text", box "inspect evidence"
        "changed_at", box "2026-11-02T12:00:00.000000Z AD"
        "recorded_by", box (ActorId.value actor)
    ]

let private replace column value columns =
    columns
    |> List.map (fun (name, current) -> name, if name = column then value else current)

[<Tests>]
let tests =
    testList
        "independent work projection controls"
        [
            testCase "SQL work readers reject every private index mismatch" (fun () ->
                Expect.equal
                    (fromColumns WorkStorage.workRow workColumns)
                    workRow
                    "Independent SQL-shaped positive witness"

                for name, value in
                    [
                        "responsible_actor",
                        box (System.Guid.Parse "95000000-0000-4000-8000-000000000005")
                        "handling_summary", box "Changed"
                        "search_text", box "changed"
                    ] do
                    Expect.throwsT<RecordCorruptException>
                        (fun () ->
                            fromColumns WorkStorage.workRow (replace name value workColumns)
                            |> ignore)
                        name

                let privateColumns =
                    set
                        [
                            "snapshot"
                            "responsible_actor"
                            "handling_summary"
                            "search_text"
                            "summary_changed_at"
                            "summary_recorded_by"
                        ]

                let erased =
                    workColumns
                    |> List.map (fun (name, value) ->
                        name,
                        if Set.contains name privateColumns then
                            box System.DBNull.Value
                        else
                            value)

                let restored = fromColumns WorkStorage.workRow erased
                Expect.isNone restored.Snapshot "The causal version survives without payload"

                for name in [ "responsible_actor"; "handling_summary"; "search_text" ] do
                    let value = workColumns |> List.find (fst >> (=) name) |> snd

                    Expect.throwsT<RecordCorruptException>
                        (fun () ->
                            fromColumns WorkStorage.workRow (replace name value erased) |> ignore)
                        "Erased columns cannot reacquire content")
            testCase "SQL reminder readers reject every private index mismatch" (fun () ->
                Expect.equal
                    (fromColumns WorkStorage.reminderRow reminderColumns)
                    reminderRow
                    "Independent SQL-shaped positive witness"

                for name, value in
                    [
                        "state", box "COMPLETED"
                        "action", box "Changed"
                        "due", box "2026-11-11 AD"
                        "search_text", box "changed"
                    ] do
                    Expect.throwsT<RecordCorruptException>
                        (fun () ->
                            fromColumns
                                WorkStorage.reminderRow
                                (replace name value reminderColumns)
                            |> ignore)
                        name)
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
                            SnapshotBytes = Array.empty

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
                            SnapshotBytes = Array.empty


                        }

                    let tombstone =
                        { reminderRow with
                            Snapshot = None
                            SnapshotBytes = Array.empty


                        }

                    Expect.isOk
                        (reminderRows (Map.ofList [ (reminderId, erased) ]) [ tombstone ])
                        "Erased ID and causal revision remain reserved"

                    Expect.isError
                        (reminderRows (Map.ofList [ (reminderId, erased) ]) [ reminderRow ])
                        "Erased payload cannot reappear")
        ]
