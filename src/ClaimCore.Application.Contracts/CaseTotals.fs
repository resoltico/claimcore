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

[<RequireQualifiedAccess>]
type CaseGroupKey =
    | Month of CaseMonth
    | Outcome of ClosureOutcome
    | Responsible of ActorRef
    | DaysOpen of DaysOpenBand
    | Missing of MissingCaseGroup

module CaseGroupKey =
    let token =
        function
        | CaseGroupKey.Month month -> CaseMonth.format month
        | CaseGroupKey.Outcome outcome -> Vocabulary.token ClosureOutcome.vocabulary outcome
        | CaseGroupKey.Responsible account -> ActorId.format account.ActorId
        | CaseGroupKey.DaysOpen band -> Vocabulary.token DaysOpenBand.vocabulary band
        | CaseGroupKey.Missing group -> Vocabulary.token MissingCaseGroup.vocabulary group

type CaseMoneyGroup =
    {
        Group: CaseGroupKey
        Count: int64
        Claimed: CurrencyTotal list
        Payable: CurrencyTotal list
        NetPaid: CurrencyTotal list
    }

type CaseMoneyRow =
    {
        Currency: Currency option
        Claimed: CurrencyTotal option
        Payable: CurrencyTotal option
        NetPaid: CurrencyTotal option
    }

module CaseMoneyRow =
    let rows claimed payable netPaid =
        let map (totals: CurrencyTotal list) =
            totals |> List.map (fun total -> total.Currency, total) |> Map.ofList

        let claimed, payable, netPaid = map claimed, map payable, map netPaid

        let currencies =
            [ claimed; payable; netPaid ]
            |> List.collect (Map.keys >> Seq.toList)
            |> List.distinct
            |> List.sortBy Currency.format

        (if currencies.IsEmpty then
             [ None ]
         else
             List.map Some currencies)
        |> List.map (fun key ->
            {
                CaseMoneyRow.Currency = key
                Claimed = key |> Option.bind (fun currency -> Map.tryFind currency claimed)
                Payable = key |> Option.bind (fun currency -> Map.tryFind currency payable)
                NetPaid = key |> Option.bind (fun currency -> Map.tryFind currency netPaid)
            })

type CaseWorklistSummary =
    {
        Basis: WorkDateBasis
        Counts: CaseWorklistCounts
        Claimed: CurrencyTotal list
        Payable: CurrencyTotal list
        NetPaid: CurrencyTotal list
        GroupBy: CaseGrouping option
        Groups: CaseMoneyGroup list
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

    let combineTotals (entries: CurrencyTotal list) =
        entries
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

    let private totals select (groups: CaseCountGroup list) =
        groups |> List.choose select |> combineTotals

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
                        GroupBy = None
                        Groups = []
                    }))
