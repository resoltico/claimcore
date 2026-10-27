namespace ClaimCore.Domain


open System

[<Sealed; NoComparison>]
type CaseCommand =
    member Authored: UnvalidatedCaseRequest
    member Reference: CaseReference
    member Act: ValidCaseAct
    member OperationId: Guid
    member ExpectedRevision: int64

[<NoComparison>]
type CaseRecord

module Case =
    val canonical: CaseCommand -> CaseCommand
    val parse: UnvalidatedCaseRequest -> Result<CaseCommand, Refusal>
    val ofAccepted: CaseSnapshot -> Result<CaseRecord, RecordViolation>
    val snapshot: CaseRecord -> CaseSnapshot

    val decide: DateOnly -> CaseCommand -> CaseRecord option -> Result<CaseRecord, Refusal>

    val eligibleActs: CaseRecord -> CaseActKind list
    val correctionActions: CaseRecord -> RecordGroup -> CorrectionGroupAction list
