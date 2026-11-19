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

type CaseMoneyRow =
    {
        Group: CaseGroupKey option
        Count: int64
        Currency: Currency option
        Claimed: CurrencyTotal option
        Payable: CurrencyTotal option
        NetPaid: CurrencyTotal option
    }

type CaseWorklistSummary =
    {
        Basis: WorkDateBasis
        Counts: CaseWorklistCounts
        GroupBy: CaseGrouping option
        Rows: CaseMoneyRow list
        NextCursor: string option
    }

module CaseWorklistCounts =
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

    let fromGroups (groups: CaseCountGroup list) =
        let total = groups |> List.sumBy (fun group -> BigInteger group.Count)

        if
            total > BigInteger Sequence.Maximum
            || groups |> List.exists (fun group -> group.Count < 0L)
        then
            Error "Invalid case count group."
        else
            Ok
                {
                    Assignment =
                        counts (Vocabulary.all CaseAssignment.vocabulary) _.Responsibility groups
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
                    Statuses = counts (Vocabulary.all CaseStatus.vocabulary) _.Status groups
                    Progress = counts (Vocabulary.all CaseProgress.vocabulary) _.Progress groups
                }
