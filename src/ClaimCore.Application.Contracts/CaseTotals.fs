namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

open System.Numerics

type ValueCount<'value> = { Value: 'value; Count: int64 }

type CurrencyTotal =
    {
        Currency: Currency
        Amount: DecimalTotal
        Cases: int64
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
        NetPaid: CurrencyTotal list
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
                            Cases = entries |> List.sumBy _.Cases
                        }
                        :: collected)))
            (Ok [])
        |> Result.map List.rev

    let private valid (group: CaseCountGroup) =
        group.Count >= 0L
        && (group.Lifecycle <> Lifecycle.Erased
            || (group.Claimed.IsNone && group.Payable.IsNone))
        && ([ group.Claimed; group.Payable ]
            |> List.choose id
            |> List.forall (fun total -> total.Cases >= 0L && total.Cases <= group.Count))

    let fromGroups basis (netPaid: CurrencyTotal list) (groups: CaseCountGroup list) =
        let total = groups |> List.sumBy (fun group -> BigInteger group.Count)

        if
            total > BigInteger Sequence.Maximum
            || not (groups |> List.forall valid)
            || (netPaid |> List.distinctBy _.Currency |> List.length) <> netPaid.Length
            || (netPaid
                |> List.exists (fun value -> value.Cases < 0L || BigInteger value.Cases > total))
        then
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
                        NetPaid = netPaid
                    }))
