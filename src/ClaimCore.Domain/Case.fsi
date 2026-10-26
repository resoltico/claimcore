namespace ClaimCore.Domain

open ClaimCore.Contract

open System

[<NoComparison>]
type CaseRecord

module Case =
    val canonical: CaseRequest -> CaseRequest
    val validateRequest: CaseRequest -> Result<unit, Refusal>
    val ofAccepted: CaseSnapshot -> Result<CaseRecord, RecordViolation>
    val snapshot: CaseRecord -> CaseSnapshot
    val decide: DateOnly -> CaseRequest -> CaseRecord option -> Result<CaseRecord, Refusal>
    val eligibleActs: CaseRecord -> CaseActKind list
    val correctionActions: CaseRecord -> RecordGroup -> CorrectionGroupAction list
