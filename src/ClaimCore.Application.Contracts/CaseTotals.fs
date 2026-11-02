namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

open System.Numerics

type ValueCount<'value> = { Value: 'value; Count: int64 }

type CurrencyTotal =
    {
        Currency: Currency
        Amount: DecimalTotal
    }

type CaseCountGroup =
    {
        Responsibility: CaseAssignment option
        Due: CaseDue
        Lifecycle: Lifecycle
        Status: CaseStatus option
        Progress: CaseProgress option
        Count: int64
        Claimed: CurrencyTotal option
        Payable: CurrencyTotal option
    }

type CaseWorklistCounts =
    {
        Assignment: ValueCount<CaseAssignment> list
        Due: ValueCount<CaseDue> list
        Total: int64
        Lifecycles: ValueCount<Lifecycle> list
        Statuses: ValueCount<CaseStatus> list
        Progress: ValueCount<CaseProgress> list
    }

type CaseWorklistSummary =
    {
        Basis: WorkDateBasis
        Counts: CaseWorklistCounts
        Claimed: CurrencyTotal list
        Payable: CurrencyTotal list
    }

module CaseWorklistSummary =
    let private counts values select (groups: CaseCountGroup list) =
        values
        |> List.map (fun value ->
            {
                ValueCount.Value = value
                Count =
                    groups
                    |> List.sumBy (fun group ->
                        if select group = Some value then group.Count else 0L)
            })

    let private totals select (groups: CaseCountGroup list) =
        groups
        |> List.choose select
        |> List.groupBy _.Currency
        |> List.sortBy (fst >> Currency.format)
        |> List.fold
            (fun state (currency, entries) ->
                state
                |> Result.bind (fun collected ->
                    DecimalTotal.sum (entries |> List.map _.Amount)
                    |> Result.mapError (fun _ -> "Case totals exceed their exact decimal range.")
                    |> Result.map (fun amount ->
                        {
                            CurrencyTotal.Currency = currency
                            Amount = amount
                        }
                        :: collected)))
            (Ok [])
        |> Result.map List.rev

    let private valid (group: CaseCountGroup) =
        let actionable =
            group.Lifecycle = Lifecycle.Active && group.Status = Some CaseStatus.Opened

        let dueActive = group.Due <> CaseDue.Inactive

        group.Count >= 0L
        && actionable = dueActive
        && (group.Lifecycle <> Lifecycle.Erased || group.Responsibility.IsNone)
        && (match group.Lifecycle with
            | Lifecycle.Erased ->
                group.Status.IsNone
                && group.Progress.IsNone
                && group.Claimed.IsNone
                && group.Payable.IsNone
            | Lifecycle.Active
            | Lifecycle.Voided ->
                group.Status.IsSome
                && group.Progress.IsSome
                && group.Claimed.IsSome
                && ((group.Progress = Some CaseProgress.Undecided) = group.Payable.IsNone))

    let fromGroups basis (groups: CaseCountGroup list) =
        let total = groups |> List.sumBy (fun group -> BigInteger group.Count)

        if total > BigInteger Sequence.Maximum || not (groups |> List.forall valid) then
            Error "Invalid case count group."
        else
            totals _.Claimed groups
            |> Result.bind (fun claimed ->
                totals _.Payable groups
                |> Result.map (fun payable ->
                    {
                        Basis = basis
                        Counts =
                            {
                                Assignment =
                                    counts
                                        (Vocabulary.all CaseAssignment.vocabulary)
                                        _.Responsibility
                                        groups
                                Due =
                                    counts
                                        (Vocabulary.all CaseDue.vocabulary)
                                        (fun group -> Some group.Due)
                                        groups
                                Total = int64 total
                                Lifecycles =
                                    counts
                                        (Vocabulary.all Lifecycle.vocabulary)
                                        (fun group -> Some group.Lifecycle)
                                        groups
                                Statuses =
                                    counts (Vocabulary.all CaseStatus.vocabulary) _.Status groups
                                Progress =
                                    counts
                                        (Vocabulary.all CaseProgress.vocabulary)
                                        _.Progress
                                        groups
                            }
                        Claimed = claimed
                        Payable = payable
                    }))
