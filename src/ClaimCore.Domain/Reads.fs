namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type HistoryAccess =
    | None
    | Summary
    | Full

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module HistoryAccess =
    let vocabulary =
        Vocabulary.ofUnion<HistoryAccess> "historyAccess" TokenCasing.ScreamingSnake None

/// The access/lifecycle snapshot used to disclose a timeline page, including an empty page.
[<RequireQualifiedAccess>]
type HistoryDisclosure =
    | Full
    | Summary
    | Erased

module HistoryDisclosure =
    let vocabulary =
        Vocabulary.ofUnion<HistoryDisclosure> "historyDisclosure" TokenCasing.ScreamingSnake None

    let ofAccess erased full =
        if erased then HistoryDisclosure.Erased
        elif full then HistoryDisclosure.Full
        else HistoryDisclosure.Summary

[<RequireQualifiedAccess>]
type CaseListOrder =
    | Reference
    | Recent
    | Received
    | FollowUp

[<RequireQualifiedAccess>]
type CaseAssignment =
    | Mine
    | Unassigned

module CaseAssignment =
    let vocabulary =
        Vocabulary.ofUnion<CaseAssignment> "assignment" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

    let plan =
        function
        | CaseAssignment.Mine -> WorkSelection.Fact WorkPredicate.Mine
        | CaseAssignment.Unassigned ->
            WorkSelection.All
                [
                    WorkSelection.Not(WorkSelection.Fact(WorkPredicate.Lifecycle Lifecycle.Erased))
                    WorkSelection.Fact WorkPredicate.Unassigned
                ]

[<RequireQualifiedAccess>]
type CaseDue =
    | Overdue
    | Today
    | Upcoming
    | Unscheduled
    | Inactive

module CaseDue =
    let vocabulary = Vocabulary.ofUnion<CaseDue> "due" TokenCasing.ScreamingSnake None
    let token = Vocabulary.token vocabulary

    let plan value =
        let active timing =
            WorkSelection.All [ WorkSelection.activeOpen; WorkSelection.Fact timing ]

        match value with
        | CaseDue.Inactive -> WorkSelection.Not WorkSelection.activeOpen
        | CaseDue.Overdue -> active (WorkPredicate.NextDue ReminderDue.Overdue)
        | CaseDue.Today -> active (WorkPredicate.NextDue ReminderDue.Today)
        | CaseDue.Upcoming -> active (WorkPredicate.NextDue ReminderDue.Upcoming)
        | CaseDue.Unscheduled -> active WorkPredicate.NoNextDue

    let at (today: DateOnly) lifecycle status (nextDue: CalendarDate option) =
        Vocabulary.all vocabulary
        |> List.find (fun value -> WorkSelection.timing today lifecycle status nextDue (plan value))

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseListOrder =
    let vocabulary =
        Vocabulary.ofUnion<CaseListOrder> "caseOrder" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<Struct>]
type CaseSearchText = private CaseSearchText of string

module CaseSearchText =
    [<Literal>]
    let MaximumCharacters = 100

    let value (CaseSearchText value) = value

    let parse raw =
        ScalarText.parse (Some PartyName.MaximumBytes) MaximumCharacters raw
        |> Result.bind (ScalarText.admit MaximumCharacters)
        |> Result.map (TextSearch.normalize >> CaseSearchText)

/// Literal current-name lookup shares the established NFKC/invariant-lower search meaning.
[<Struct>]
type DirectorySearchText = private DirectorySearchText of string

module DirectorySearchText =
    let MaximumCharacters = PartyName.MaximumCharacters
    let value (DirectorySearchText value) = value

    let parse raw =
        PartyName.parse raw
        |> Result.bind PartyName.admit
        |> Result.map (PartyName.format >> TextSearch.normalize >> DirectorySearchText)

    let matches query name =
        TextSearch.normalize name
        |> fun name -> name.Contains(value query, System.StringComparison.Ordinal)

type CaseListFilter<'text> =
    {
        Lifecycles: Lifecycle list
        Statuses: CaseStatus list
        Progress: CaseProgress list
        Text: 'text option
        Order: CaseListOrder
        View: CaseView option
        ResponsibleActor: ActorId option
        Assignment: CaseAssignment option
        Due: CaseDue option
    }

module CaseListFilter =
    /// Independent supplied filters intersect; members of one filter are alternatives.
    let selection (filter: CaseListFilter<'text>) =
        let alternatives fact values =
            if List.isEmpty values then
                WorkSelection.All []
            else
                values |> List.map (fact >> WorkSelection.Fact) |> WorkSelection.Any

        WorkSelection.All
            [
                alternatives WorkPredicate.Lifecycle filter.Lifecycles
                alternatives WorkPredicate.Status filter.Statuses
                alternatives WorkPredicate.Progress filter.Progress
                filter.View
                |> Option.map CaseView.plan
                |> Option.defaultValue (WorkSelection.All [])
                filter.Assignment
                |> Option.map CaseAssignment.plan
                |> Option.defaultValue (WorkSelection.All [])
                filter.Due
                |> Option.map CaseDue.plan
                |> Option.defaultValue (WorkSelection.All [])
                WorkSelection.Fact(WorkPredicate.Responsible filter.ResponsibleActor)
            ]

    let create lifecycles statuses progress text order =
        {
            Lifecycles = lifecycles
            Statuses = statuses
            Progress = progress
            Text = text
            Order = order
            View = None
            ResponsibleActor = None
            Assignment = None
            Due = None
        }

    let empty<'text> : CaseListFilter<'text> =
        create [] [] [] None CaseListOrder.Reference

    /// The person responsibility count and its offered list selection share this domain meaning.
    let activeOpen<'text> : CaseListFilter<'text> =
        let lifecycle, status = CaseState.actionable
        create [ lifecycle ] [ status ] [] None CaseListOrder.Reference

    let admit (input: CaseListFilter<string>) =
        let text =
            match input.Text with
            | None -> Ok None
            | Some raw -> CaseSearchText.parse raw |> Result.map Some

        text
        |> Result.map (fun text ->
            create
                (input.Lifecycles |> List.distinct |> List.sort)
                (input.Statuses |> List.distinct |> List.sort)
                (input.Progress |> List.distinct |> List.sort)
                text
                input.Order
            |> fun filter ->
                { filter with
                    View = input.View
                    ResponsibleActor = input.ResponsibleActor
                    Assignment = input.Assignment
                    Due = input.Due
                })

    let matches (filter: CaseListFilter<CaseSearchText>) lifecycle status progress text =
        let allows choices candidate =
            List.isEmpty choices
            || (candidate |> Option.exists (fun value -> List.contains value choices))

        allows filter.Lifecycles (Some lifecycle)
        && allows filter.Statuses status
        && allows filter.Progress progress
        && (filter.Text
            |> Option.forall (fun query ->
                text
                |> Option.exists (fun (text: string) ->
                    text.Contains(CaseSearchText.value query, StringComparison.Ordinal))))
