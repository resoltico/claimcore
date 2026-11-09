namespace ClaimCore.Domain


open System

[<Sealed; NoEquality; NoComparison>]
type PreparedCaseInput

[<Sealed; NoComparison>]
type CaseCommand =
    member Authored: UnvalidatedCaseRequest
    member Reference: CaseReference
    member Act: ValidCaseAct
    member OperationId: OperationId
    member ExpectedRevision: Revision

[<NoComparison>]
type CaseRecord

module Case =
    val canonical: CaseCommand -> CaseCommand
    val prepare: UnvalidatedCaseRequest -> PreparedCaseInput
    val parsed: PreparedCaseInput -> Result<CaseCommand, Refusal>
    val parse: UnvalidatedCaseRequest -> Result<CaseCommand, Refusal>

    val admitPrepared:
        DateOnly -> CaseSnapshot option -> PreparedCaseInput -> Result<CaseCommand, Refusal>

    val admit:
        DateOnly -> CaseSnapshot option -> UnvalidatedCaseRequest -> Result<CaseCommand, Refusal>

    val ofAccepted: CaseSnapshot -> Result<CaseRecord, RecordViolation>
    val snapshot: CaseRecord -> CaseSnapshot

    val decide: DateOnly -> CasePaymentContext -> CaseCommand -> CaseRecord option -> Result<CaseDecision, CaseDecisionError>

    val decideSnapshot:
        DateOnly -> CasePaymentContext -> CaseCommand -> CaseSnapshot option -> Result<CaseDecision, CaseDecisionError>

    val actRefusal: CaseActKind -> CaseRecord -> CaseRefusal option
    val eligibleActs: CaseRecord -> CaseActKind list
    val correctionActions: CaseRecord -> RecordGroup -> CorrectionGroupAction list
