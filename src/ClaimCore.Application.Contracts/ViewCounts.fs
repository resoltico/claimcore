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
