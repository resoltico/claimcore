namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// The bounded command retains exactly one target and at most one replacement.
type ReminderRequestRecord =
    {
        OperationId: OperationId
        Command: ReminderCommand
    }

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

    let private act =
        CanonicalInputs.reminderAct
        |> mapResult
            (fun path input ->
                ReminderInput.parse input
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid reminder input."
                    }))
            ReminderInput.authored

    let private parent request =
        match request.Command with
        | ReminderCommand.Create(parent, _)
        | ReminderCommand.Change(parent, _, _, _) -> parent

    let private projectAct request = ReminderInput.ofCommand request.Command

    let private requestCodec =
        (record (fun operation parent act ->
            {
                ReminderRequestRecord.OperationId = operation
                Command = ReminderInput.resolve parent act
            })
        : Fields<ReminderRequestRecord, _>)
        |> field "operationId" _.OperationId operationId
        |> field "caseId" parent caseId
        |> field "act" projectAct act
        |> build

    let private effectsCodec =
        let effects =
            taggedOn
                "kind"
                "ReminderEffectsRecord"
                [
                    variant
                        "CREATED"
                        ReminderEffects.Created
                        (function
                        | ReminderEffects.Created value -> Some value
                        | _ -> None)
                        (record id |> field "reminder" id reminder)
                    variant
                        "CHANGED"
                        ReminderEffects.Changed
                        (function
                        | ReminderEffects.Changed value -> Some value
                        | _ -> None)
                        (record id |> field "reminder" id reminder)
                    variant
                        "COMPLETED_AND_PLANNED"
                        ReminderEffects.CompletedAndPlanned
                        (function
                        | ReminderEffects.CompletedAndPlanned(completed, next) ->
                            Some(completed, next)
                        | _ -> None)
                        (record (fun completed next -> completed, next)
                         |> field "completed" fst reminder
                         |> field "next" snd reminder)
                ]

        record id |> field "effects" id effects |> build

    let encodeRequest request =
        requestCodec.Write request |> CanonicalWriter.bytes |> RecordLimits.requestBytes

    let decodeRequest (bytes: byte array) =
        if bytes.Length > RecordLimits.MaximumRequestRecordBytes then
            Error
                {
                    Field = "$"
                    Message = "Retained request exceeds its byte bound."
                }
        else
            decode requestCodec bytes

    let encodeEffects effects =
        effectsCodec.Write effects |> CanonicalWriter.bytes

    let decodeEffects bytes = decode effectsCodec bytes

    let private snapshotCodec = record id |> field "reminder" id reminder |> build

    let encodeSnapshot value =
        snapshotCodec.Write value |> CanonicalWriter.bytes

    let decodeSnapshot bytes = decode snapshotCodec bytes
