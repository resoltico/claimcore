namespace ClaimCore.Domain

open System

type CaseIndex =
    {
        Responsible: ActorId option
        FollowUp: CalendarDate option
        Status: CaseStatus option
        Progress: CaseProgress option
        ChangedAt: DateTimeOffset
        SearchText: string option
        WorkSearchText: string option
        ReceivedOn: CalendarDate option
        Claimed: Money option
        Payable: Money option
        PaymentReview: bool option
        Outcome: ClosureOutcome option
    }

module CaseIndex =
    let create
        changedAt
        (snapshot: CaseSnapshot)
        (work: CaseWorkSnapshot option)
        pendingDue
        parties
        =
        let fields = CaseState.fields snapshot.State

        let work = if snapshot.State = CaseState.Erased then None else work
        let responsible, _, workSearch = CaseWorkSnapshot.columns work

        {
            Responsible = responsible
            FollowUp =
                if snapshot.State = CaseState.Erased then
                    None
                else
                    pendingDue |> List.sort |> List.tryHead
            Status = fields |> Option.map _.Status
            Outcome = fields |> Option.bind (_.Handling >> Handling.closure) |> Option.map _.Outcome
            Progress = fields |> Option.map (fun fields -> CaseProgress.ofFinancial fields.Finance)
            ChangedAt = changedAt
            SearchText = fields |> Option.map (CaseSearch.text parties)
            WorkSearchText = workSearch
            ReceivedOn =
                fields
                |> Option.bind (fun fields -> Registration.receivedDate fields.Registration)
            Claimed = fields |> Option.bind (fun fields -> Registration.claimed fields.Registration)
            PaymentReview =
                fields
                |> Option.map (fun fields -> FinancialPosition.needsReview fields.Finance)
            Payable =
                fields
                |> Option.bind (fun fields -> fields.Decision |> Option.map Decision.payable)
        }
