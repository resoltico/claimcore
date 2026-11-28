namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

type PaymentPayee =
    {
        Id: PartyId
        Name: PartyName option
        Removed: bool
    }

type CasePaymentItem =
    {
        Entry: PaymentEntry
        Position: EffectPosition
        Actions: TaskId list
    }

type CasePaymentPage =
    {
        Basis: WorkDateBasis
        CaseId: CaseId
        Reference: CaseReference
        Revision: Revision
        WorkRevision: Revision
        State: CaseState<FinancialPosition>
        ClosureOutcome: ClosureOutcome option
        Items: CasePaymentItem list
        Payees: PaymentPayee list
        NextCursor: string option
    }
