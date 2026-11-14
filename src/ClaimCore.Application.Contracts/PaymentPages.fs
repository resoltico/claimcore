namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

type CasePaymentItem =
    {
        Entry: PaymentEntry
        Position: EffectPosition
        CanRemove: bool
    }

type CasePaymentPage =
    {
        Basis: WorkDateBasis
        CaseId: CaseId
        Reference: CaseReference
        Revision: Revision
        WorkRevision: Revision
        State: CaseState<FinancialPosition>
        Items: CasePaymentItem list
        NextCursor: string option
    }
