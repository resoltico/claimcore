namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

module ReminderRecord =
    open Codec
    open RecordScalars

    let private reminder =
        (record Reminder.decode: Fields<Reminder, _>)
        |> field "reminderId" Reminder.id reminderId
        |> field "caseId" Reminder.caseId caseId
        |> field "revision" Reminder.revision (revision 1L)
        |> field "action" Reminder.action action
        |> field "due" Reminder.due date
        |> field "kind" Reminder.kind (VocabularyCodec.create ReminderKind.vocabulary)
        |> field
            "state"
            Reminder.state
            (enumeration
                [
                    ReminderState.Pending, "PENDING"
                    ReminderState.Completed, "COMPLETED"
                    ReminderState.Cancelled, "CANCELLED"
                ])
        |> optionalField "doneOn" Reminder.doneOn date
        |> buildResult (fun path reason -> { Path = path; Reason = reason })

    let private snapshotCodec = record id |> field "reminder" id reminder |> build

    let encodeSnapshot value =
        snapshotCodec.Write value |> CanonicalWriter.bytes

    let decodeSnapshot bytes =
        Codec.decode Compartment.MaximumContentBytes snapshotCodec bytes
        |> Result.mapError (fun problem ->
            {
                DecodeProblem.Field = problem.Path
                Message = problem.Reason
            })
