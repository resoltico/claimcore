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
    | PartyNew of CaseSelector
    | Party of CaseSelector * PartyId * Revision
    | LinkNew of CaseSelector
    | Link of CaseSelector * LinkId
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
        | Binding.Note(target, _)
        | Binding.PartyNew target
        | Binding.Party(target, _, _)
        | Binding.LinkNew target
        | Binding.Link(target, _) -> Some target
        | _ -> None

[<RequireQualifiedAccess>]
type BindingShape =
    | CaseNew
    | Case
    | CasePayment
    | CaseErase
    | Work
    | ReminderNew
    | Reminder
    | NoteNew
    | Note
    | PartyNew
    | Party
    | LinkNew
    | Link
    | PeopleNew
    | People

module BindingShape =
    let ofValue =
        function
        | Binding.CaseNew _ -> BindingShape.CaseNew
        | Binding.Case _ -> BindingShape.Case
        | Binding.CasePayment _ -> BindingShape.CasePayment
        | Binding.CaseErase _ -> BindingShape.CaseErase
        | Binding.Work _ -> BindingShape.Work
        | Binding.ReminderNew _ -> BindingShape.ReminderNew
        | Binding.Reminder _ -> BindingShape.Reminder
        | Binding.NoteNew _ -> BindingShape.NoteNew
        | Binding.Note _ -> BindingShape.Note
        | Binding.PartyNew _ -> BindingShape.PartyNew
        | Binding.Party _ -> BindingShape.Party
        | Binding.LinkNew _ -> BindingShape.LinkNew
        | Binding.Link _ -> BindingShape.Link
        | Binding.PeopleNew _ -> BindingShape.PeopleNew
        | Binding.People _ -> BindingShape.People
