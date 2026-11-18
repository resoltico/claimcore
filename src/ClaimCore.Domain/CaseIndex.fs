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
        Paid: Money option
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
            Progress = fields |> Option.map (fun fields -> CaseProgress.ofFinancial fields.Finance)
            ChangedAt = changedAt
            SearchText = fields |> Option.map (CaseSearch.text parties)
            WorkSearchText = workSearch
            ReceivedOn =
                fields
                |> Option.bind (fun fields -> Registration.receivedDate fields.Registration)
            Claimed = fields |> Option.bind (fun fields -> Registration.claimed fields.Registration)
            Paid =
                fields
                |> Option.bind (fun fields ->
                    FinancialPosition.totals fields.Finance |> Option.map _.Paid)
            Payable =
                fields
                |> Option.bind (fun fields -> fields.Decision |> Option.map Decision.payable)
        }
