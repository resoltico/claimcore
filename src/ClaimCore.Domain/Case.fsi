namespace ClaimCore.Domain


open System

[<NoComparison>]
type CaseRecord

module Case =
    val canonical: UnvalidatedCaseRequest -> UnvalidatedCaseRequest
    val validateRequest: UnvalidatedCaseRequest -> Result<unit, Refusal>
    val ofAccepted: CaseSnapshot -> Result<CaseRecord, RecordViolation>
    val snapshot: CaseRecord -> CaseSnapshot

    val decide:
        DateOnly -> UnvalidatedCaseRequest -> CaseRecord option -> Result<CaseRecord, Refusal>

    val eligibleActs: CaseRecord -> CaseActKind list
    val correctionActions: CaseRecord -> RecordGroup -> CorrectionGroupAction list
