namespace ClaimCore.Domain

/// Authored references remain distinct from retained IDs, including UUID-shaped references.
[<RequireQualifiedAccess>]
type CaseSelector =
    | Reference of string
    | Id of CaseId

/// Reviewed target and concurrency facts. Authored task values belong to InputMap.
[<RequireQualifiedAccess>]
type Binding =
    | CaseNew of Revision
    | Case of CaseSelector * Revision
    | CasePayment of CaseSelector * Revision * OperationId
    | CaseErase of CaseSelector * Revision * Sequence
    | Work of CaseSelector * Revision
    | ReminderNew of CaseSelector
    | Reminder of CaseSelector * ReminderId * Revision
    | NoteNew of CaseSelector
    | Note of CaseSelector * NoteId
    | PeopleNew of Revision
    | People of ActorId * Revision

module Binding =
    let caseTarget =
        function
        | Binding.Case(target, _)
        | Binding.CasePayment(target, _, _)
        | Binding.CaseErase(target, _, _)
        | Binding.Work(target, _)
        | Binding.ReminderNew target
        | Binding.Reminder(target, _, _)
        | Binding.NoteNew target
        | Binding.Note(target, _) -> Some target
        | _ -> None
