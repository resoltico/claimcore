namespace ClaimCore.Contract

/// Accepted facts share the authored group types; a decision is complete or absent.
type CaseFields =
    {
        CaseReference: string
        Status: CaseStatus
        Registration: Registration
        Decision: Decision option
        Payment: Payment option
    }

[<RequireQualifiedAccess>]
type CaseView =
    | Live of fields: CaseFields * disposition: Disposition
    | Erased

type CaseSnapshot =
    {
        Reference: string
        Revision: int64
        View: CaseView
    }
