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

[<RequireQualifiedAccess>]
type CaseAssignment = | AssignedToMe

module CaseAssignment =
    let vocabulary =
        Vocabulary.ofUnion<CaseAssignment> "assignment" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type CaseProgress =
    | Undecided
    | Decided
    | Paid

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseListOrder =
    let vocabulary =
        Vocabulary.ofUnion<CaseListOrder> "caseOrder" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseProgress =
    let vocabulary =
        Vocabulary.ofUnion<CaseProgress> "progress" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

    let ofPayment =
        function
        | PaymentProgress.Undecided -> CaseProgress.Undecided
        | PaymentProgress.Decided _ -> CaseProgress.Decided
        | PaymentProgress.Paid _ -> CaseProgress.Paid

module CaseSearch =
    let normalize (value: string) =
        value.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant()

    let text fields =
        CaseFields.declarations
        |> List.filter _.Searchable
        |> List.choose (fun row -> CaseFields.value row.Field fields)
        |> List.map normalize
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
        |> Result.map (CaseSearch.normalize >> CaseSearchText)

type CaseListFilter<'text> =
    {
        Lifecycles: Lifecycle list
        Statuses: CaseStatus list
        Progress: CaseProgress list
        Text: 'text option
        Order: CaseListOrder
        Assignment: CaseAssignment option
    }

module CaseListFilter =
    let create lifecycles statuses progress text order =
        {
            Lifecycles = lifecycles
            Statuses = statuses
            Progress = progress
            Text = text
            Order = order
            Assignment = None
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
                    Assignment = input.Assignment
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
