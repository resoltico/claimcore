namespace ClaimCore.Domain

open System

/// Accepted business state is constructed only by the Domain.
[<NoComparison>]
type Claim

[<RequireQualifiedAccess; NoComparison>]
type CaseState =
    | Live of claim: Claim * disposition: Disposition
    | Erased

[<NoComparison>]
type CaseRecord

module Case =
    val validateRequest: CaseRequest -> Result<unit, CaseRefusal>
    val ofAccepted: CaseSnapshot -> Result<CaseRecord, RecordViolation>
    val snapshot: CaseRecord -> CaseSnapshot
    val decide: DateOnly -> CaseRequest -> CaseRecord option -> Result<CaseRecord, CaseRefusal>
    val eligibleActs: CaseRecord -> CaseActKind list
    val correctionActions: CaseRecord -> CorrectionGroup -> CorrectionGroupAction list
