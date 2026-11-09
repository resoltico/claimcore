namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

module ReminderRecord =
    open Codec
    open WorkRecordCodecs

    let private reminder =
        (record (fun id parent revision action due state ->
            Reminder.ofAccepted id parent revision action due state
            |> Result.defaultWith invalidOp)
        : Fields<Reminder, _>)
        |> field "reminderId" Reminder.id reminderId
        |> field "caseId" Reminder.caseId caseId
        |> field "revision" Reminder.revision (revision 1L)
        |> field "action" Reminder.action action
        |> field "due" Reminder.due date
        |> field
            "state"
            Reminder.state
            (enumeration
                [
                    ReminderState.Pending, "PENDING"
                    ReminderState.Completed, "COMPLETED"
                    ReminderState.Cancelled, "CANCELLED"
                ])
        |> build

    let private snapshotCodec = record id |> field "reminder" id reminder |> build

    let encodeSnapshot value =
        snapshotCodec.Write value |> CanonicalWriter.bytes

    let decodeSnapshot bytes = decode snapshotCodec bytes
