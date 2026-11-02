namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// The bounded command retains exactly one target and at most one replacement.
type ReminderRequestRecord = { OperationId: OperationId; Command: ReminderCommand }

[<RequireQualifiedAccess>]
type private RecordedReminderAct =
    | Create of ReminderPlan
    | Change of ReminderId * Revision * ReminderChange

module ReminderRecord =
    open Codec
    open WorkRecordCodecs

    let private plan =
        (record (fun action due -> { ReminderPlan.Action = action; Due = due }) : Fields<ReminderPlan, _>)
        |> field "action" _.Action action
        |> field "due" _.Due date
        |> build

    let private reminder =
        (record (fun id parent revision action due state ->
            Reminder.ofAccepted id parent revision action due state
            |> Result.defaultWith invalidOp) : Fields<Reminder, _>)
        |> field "reminderId" Reminder.id reminderId
        |> field "caseId" Reminder.caseId caseId
        |> field "revision" Reminder.revision (revision 1L)
        |> field "action" Reminder.action action
        |> field "due" Reminder.due date
        |> field "state" Reminder.state (enumeration [ ReminderState.Pending, "PENDING"; ReminderState.Completed, "COMPLETED"; ReminderState.Cancelled, "CANCELLED" ])
        |> build

    let private changeFields construct project =
        record construct
        |> field "reminderId" (project >> fun (id, _, _) -> id) reminderId
        |> field "expectedRevision" (project >> fun (_, revision, _) -> revision) (revision 1L)

    let private act =
        let changeKind expected = function
            | RecordedReminderAct.Change(id, revision, act) when expected act -> Some(id, revision, act)
            | _ -> None

        let bare token kind =
            variant token
                (fun (id, revision) -> RecordedReminderAct.Change(id, revision, kind))
                (fun value -> changeKind ((=) kind) value |> Option.map (fun (id, revision, _) -> id, revision))
                (record (fun id revision -> id, revision)
                 |> field "reminderId" fst reminderId
                 |> field "expectedRevision" snd (revision 1L))

        let planned token inject project =
            variant token
                (fun (id, revision, plan) -> RecordedReminderAct.Change(id, revision, inject plan))
                (function
                 | RecordedReminderAct.Change(id, revision, change) -> project change |> Option.map (fun plan -> id, revision, plan)
                 | _ -> None)
                (record (fun id revision plan -> id, revision, plan)
                 |> field "reminderId" (fun (id, _, _) -> id) reminderId
                 |> field "expectedRevision" (fun (_, revision, _) -> revision) (revision 1L)
                 |> field "plan" (fun (_, _, plan) -> plan) plan)

        taggedOn "kind" "ReminderActRecord"
            [
                variant "CREATE" RecordedReminderAct.Create (function RecordedReminderAct.Create value -> Some value | _ -> None)
                    (record id |> field "plan" id plan)
                planned "UPDATE" ReminderChange.Update (function ReminderChange.Update value -> Some value | _ -> None)
                bare "COMPLETE" ReminderChange.Complete
                bare "CANCEL" ReminderChange.Cancel
                variant "REOPEN"
                    (fun (id, revision, due) -> RecordedReminderAct.Change(id, revision, ReminderChange.Reopen due))
                    (function RecordedReminderAct.Change(id, revision, ReminderChange.Reopen due) -> Some(id, revision, due) | _ -> None)
                    (record (fun id revision due -> id, revision, due)
                     |> field "reminderId" (fun (id, _, _) -> id) reminderId
                     |> field "expectedRevision" (fun (_, revision, _) -> revision) (revision 1L)
                     |> field "due" (fun (_, _, due) -> due) date)
                planned "COMPLETE_AND_PLAN_NEXT" ReminderChange.CompleteAndPlanNext (function ReminderChange.CompleteAndPlanNext value -> Some value | _ -> None)
            ]

    let private parent request =
        match request.Command with
        | ReminderCommand.Create(parent, _)
        | ReminderCommand.Change(parent, _, _, _) -> parent

    let private projectAct request =
        match request.Command with
        | ReminderCommand.Create(_, plan) -> RecordedReminderAct.Create plan
        | ReminderCommand.Change(_, id, revision, change) -> RecordedReminderAct.Change(id, revision, change)

    let private requestCodec =
        (record (fun operation parent act () ->
            {
                ReminderRequestRecord.OperationId = operation
                Command = match act with RecordedReminderAct.Create plan -> ReminderCommand.Create(parent, plan) | RecordedReminderAct.Change(id, revision, change) -> ReminderCommand.Change(parent, id, revision, change)
            }) : Fields<ReminderRequestRecord, _>)
        |> field "operationId" _.OperationId operationId
        |> field "caseId" parent caseId
        |> field "act" projectAct act
        |> field "format" (fun _ -> ()) (version RecordVersions.Reminder)
        |> build

    let private effectsCodec =
        let effects =
            taggedOn "kind" "ReminderEffectsRecord"
                [
                    variant "CREATED" ReminderEffects.Created (function ReminderEffects.Created value -> Some value | _ -> None)
                        (record id |> field "reminder" id reminder)
                    variant "CHANGED" ReminderEffects.Changed (function ReminderEffects.Changed value -> Some value | _ -> None)
                        (record id |> field "reminder" id reminder)
                    variant "COMPLETED_AND_PLANNED"
                        ReminderEffects.CompletedAndPlanned
                        (function ReminderEffects.CompletedAndPlanned(completed, next) -> Some(completed, next) | _ -> None)
                        (record (fun completed next -> completed, next)
                         |> field "completed" fst reminder
                         |> field "next" snd reminder)
                ]

        record (fun effects () -> effects)
        |> field "effects" id effects
        |> field "format" (fun _ -> ()) (version RecordVersions.Reminder)
        |> build

    let encodeRequest request = requestCodec.Write request |> CanonicalWriter.bytes |> RecordLimits.requestBytes
    let decodeRequest bytes = decode requestCodec bytes
    let encodeEffects effects = effectsCodec.Write effects |> CanonicalWriter.bytes
    let decodeEffects bytes = decode effectsCodec bytes
    let encodeSnapshot value = reminder.Write value |> CanonicalWriter.bytes
    let decodeSnapshot bytes = decode reminder bytes
