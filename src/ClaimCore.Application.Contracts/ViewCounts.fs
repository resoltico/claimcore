namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

type CaseViewCount = { View: CaseView; Count: int64 }
type ReminderViewCount = { View: ReminderView; Count: int64 }

type WorkViews =
    {
        Basis: WorkDateBasis
        CaseViews: CaseViewCount list
        ReminderViews: ReminderViewCount list
    }

/// Navigation totals use the same authorized snapshot and disjoint due buckets as its views.
module WorkViewCounts =
    let casesNeedingAttention (value: WorkViews) =
        value.CaseViews
        |> List.tryFind (fun item -> item.View = CaseView.NeedsAttention)
        |> Option.map _.Count

    let remindersNeedingAttention (value: WorkViews) =
        let count view =
            value.ReminderViews
            |> List.tryFind (fun item -> item.View = view)
            |> Option.map _.Count

        match count ReminderView.Overdue, count ReminderView.Today with
        | Some overdue, Some today -> Some(overdue + today)
        | _ -> None
