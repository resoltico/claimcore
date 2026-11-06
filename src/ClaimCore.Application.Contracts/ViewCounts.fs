namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

type CaseViewCount = { View: CaseView; Count: int64 }
type ReminderViewCount = { View: ReminderView; Count: int64 }

type CaseViews =
    {
        Basis: WorkDateBasis
        Items: CaseViewCount list
    }

type ReminderViews =
    {
        Basis: WorkDateBasis
        Items: ReminderViewCount list
    }
