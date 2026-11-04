namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type HistoryDetail =
    | Summary
    | Full

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module HistoryDetail =
    let vocabulary =
        Vocabulary.ofUnion<HistoryDetail> "historyDetail" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type CaseListOrder =
    | Reference
    | Recent
    | Notified
    | FollowUp

[<RequireQualifiedAccess>]
type CaseAssignment =
    | Mine
    | Unassigned

module CaseAssignment =
    let vocabulary =
        Vocabulary.ofUnion<CaseAssignment> "assignment" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

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

    let at (today: DateOnly) lifecycle status (nextDue: CalendarDate option) =
        if not (CaseState.isActionable lifecycle status) then
            CaseDue.Inactive
        else
            match nextDue with
            | None -> CaseDue.Unscheduled
            | Some date when CalendarDate.value date < today -> CaseDue.Overdue
            | Some date when CalendarDate.value date = today -> CaseDue.Today
            | Some _ -> CaseDue.Upcoming

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseListOrder =
    let vocabulary =
        Vocabulary.ofUnion<CaseListOrder> "caseOrder" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

module TextSearch =
    let normalize (value: string) =
        value.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant()

module CaseSearch =
    let private searchableFields = CaseFields.declarations |> List.filter _.Searchable

    let text fields =
        searchableFields
        |> List.choose (fun row -> CaseFields.value row.Field fields)
        |> List.map TextSearch.normalize
        |> String.concat "\u001f"

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
