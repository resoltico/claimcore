namespace ClaimCore.Contract

/// The two case statuses requested by the operator. Payment never changes this implicitly.
[<RequireQualifiedAccess>]
type CaseStatus =
    | Opened
    | Closed

/// Closed operation vocabulary; names and payload kinds belong to the core, not a renderer.
[<RequireQualifiedAccess>]
type BusinessActKind =
    | Open
    | AmendRegistration
    | Correct
    | Decide
    | WithdrawDecision
    | RecordPayment
    | ClearPayment
    | Close
    | Reopen

[<RequireQualifiedAccess>]
type StewardshipActKind =
    | Void
    | Reinstate
    | Erase

[<RequireQualifiedAccess>]
type CaseActKind =
    | Business of BusinessActKind
    | Stewardship of StewardshipActKind

/// Stewardship changes the case record without asserting business facts.
[<RequireQualifiedAccess>]
type Disposition =
    | Active
    | Voided
