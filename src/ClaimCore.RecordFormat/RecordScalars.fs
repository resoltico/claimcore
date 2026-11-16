namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// Permanent scalar representation, independently of current authoring admission.
module internal RecordScalars =
    open Codec

    let invalid path =
        Error
            {
                Path = path
                Reason = "Invalid retained scalar."
            }

    let identifier create value =
        uuid
        |> interpret
            (fun path raw -> create raw |> Option.map Ok |> Option.defaultValue (invalid path))
            value

    let operationId = identifier OperationId.create OperationId.value
    let caseId = identifier CaseId.create CaseId.value
    let actorId = identifier ActorId.create ActorId.value
    let reminderId = identifier ReminderId.create ReminderId.value

    let revision minimum =
        integer minimum SafeInteger.Maximum
        |> map (Revision.create >> Option.get) Revision.value

    let scalar parse format =
        text
        |> interpret
            (fun path raw ->
                parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid retained scalar."
                    }))
            format

    let summary = scalar HandlingSummary.parse HandlingSummary.format
    let action = scalar ReminderAction.parse ReminderAction.format
    let date = scalar CalendarDate.parse CalendarDate.format
