namespace ClaimCore.Domain

open System

/// Business disposition does not add to, or rewrite, the thirteen case fields.
[<RequireQualifiedAccess>]
type CaseDisposition =
    | Active
    | VoidedDataEntryError

[<RequireQualifiedAccess>]
type PrivacyPhase =
    | Active
    | ErasureRequested
    | ErasurePending
    | PayloadErasedSuppressionRetained
    | ErasureFinal

[<RequireQualifiedAccess>]
type LifecycleAction =
    | VoidDataEntryError
    | ReinstateVoided
    | PurgeLivePayload

/// These are authoritative identity bindings, never display names or IdP group claims.
type LifecycleApproval =
    private
        {
            Action: LifecycleAction
            OperationId: Guid
            CaseId: Guid
            ExpectedRevision: int64
            EventDigest: string
            ApproverId: Guid
            ExpiresAt: DateTimeOffset
        }

type LifecycleDecision =
    {
        Action: LifecycleAction
        OperationId: Guid
        CaseId: Guid
        ExpectedRevision: int64
        EventDigest: string
        ActorId: Guid
        At: DateTimeOffset
        Reason: string
        Approvals: LifecycleApproval list
    }

/// The schema-owner process executes live deletion as a technical principal, never as a
/// fabricated human actor. The two human stewardship approvals remain individually witnessed.
type OwnerPurgeDecision =
    {
        OperationId: Guid
        CaseId: Guid
        ExpectedRevision: int64
        EventDigest: string
        At: DateTimeOffset
        Reason: string
        Approvals: LifecycleApproval list
    }

[<RequireQualifiedAccess>]
type PaymentAssertionEvidence =
    private
    | NoHistoricalPaymentAssertionVerified
    | HistoricalPaymentAssertion

type WitnessedErasureFence =
    private | WitnessedErasureFence of caseId: Guid * ticket: Guid * digest: string

type LifecycleHold =
    {
        Id: Guid
        Ground: string
        ReviewOn: DateOnly
        RecordedBy: Guid
        RecordedAt: DateTimeOffset
    }

[<RequireQualifiedAccess>]
type LifecycleRefusal =
    | InvalidIdentity
    | InvalidReason
    | InvalidTime
    | WrongCase
    | VersionConflict
    | RevisionExhausted
    | WrongDisposition
    | ErasureHasBegun
    | WrongPrivacyPhase
    | DuplicateHold
    | HoldNotFound
    | HoldActive
    | HoldCapacityExceeded
    | ApprovalRequired
    | ApprovalCapacityExceeded
    | ApprovalMismatch
    | ApprovalExpired
    | ErasureEvidenceIncomplete

[<RequireQualifiedAccess>]
type LifecycleAccess =
    | OrdinaryRead
    | OrdinaryCommand
    | RecoveryResolve
    | Export
    | DefaultList
    | CustodianAudit

/// Only the trusted core/Application path may mint evidence after independent verification.
/// This is a compile-time construction boundary, not a claim that a caller-supplied fact is proof.
module internal LifecycleEvidenceAuthority =
    let private validDigest (digest: string) =
        not (String.IsNullOrEmpty digest)
        && digest.Length = 64
        && (digest |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))

    let noHistoricalPaymentAssertionVerified =
        PaymentAssertionEvidence.NoHistoricalPaymentAssertionVerified

    let historicalPaymentAssertion = PaymentAssertionEvidence.HistoricalPaymentAssertion

    let requiresDualPayment evidence =
        evidence = PaymentAssertionEvidence.HistoricalPaymentAssertion

    let approval action operationId caseId revision digest approverId expiry =
        {
            Action = action
            OperationId = operationId
            CaseId = caseId
            ExpectedRevision = revision
            EventDigest = digest
            ApproverId = approverId
            ExpiresAt = expiry
        }

    let witnessedFence caseId ticket digest =
        if caseId = Guid.Empty || ticket = Guid.Empty || not (validDigest digest) then
            None
        else
            Some(WitnessedErasureFence(caseId, ticket, digest))

    let matchesFence caseId (WitnessedErasureFence(boundCaseId, ticket, digest)) =
        caseId = boundCaseId && ticket <> Guid.Empty && validDigest digest

    let private bound (decision: LifecycleDecision) (approval: LifecycleApproval) =
        approval.Action = decision.Action
        && approval.OperationId = decision.OperationId
        && approval.CaseId = decision.CaseId
        && approval.ExpectedRevision = decision.ExpectedRevision
        && approval.EventDigest = decision.EventDigest

    let private distinct
        (decision: LifecycleDecision)
        (first: LifecycleApproval)
        (second: LifecycleApproval)
        =
        first.ApproverId <> second.ApproverId
        && first.ApproverId <> decision.ActorId
        && second.ApproverId <> decision.ActorId

    let private validTime (decision: LifecycleDecision) (approval: LifecycleApproval) =
        approval.ExpiresAt.Offset = TimeSpan.Zero
        && approval.ExpiresAt - decision.At <= TimeSpan.FromHours(24.0)

    let checkApprovals (decision: LifecycleDecision) =
        match decision.Approvals with
        | [ first; second ] when first.ApproverId <> Guid.Empty && second.ApproverId <> Guid.Empty ->
            if not (bound decision first && bound decision second) then
                Error LifecycleRefusal.ApprovalMismatch
            elif not (distinct decision first second) then
                Error LifecycleRefusal.ApprovalRequired
            elif first.ExpiresAt <= decision.At || second.ExpiresAt <= decision.At then
                Error LifecycleRefusal.ApprovalExpired
            elif not (validTime decision first && validTime decision second) then
                Error LifecycleRefusal.ApprovalMismatch
            else
                Ok()
        | _ -> Error LifecycleRefusal.ApprovalRequired

    let checkOwnerPurgeApprovals (decision: OwnerPurgeDecision) =
        let bound (approval: LifecycleApproval) =
            approval.Action = LifecycleAction.PurgeLivePayload
            && approval.OperationId = decision.OperationId
            && approval.CaseId = decision.CaseId
            && approval.ExpectedRevision = decision.ExpectedRevision
            && approval.EventDigest = decision.EventDigest

        let validOwnerTime (approval: LifecycleApproval) =
            approval.ExpiresAt.Offset = TimeSpan.Zero
            && approval.ExpiresAt - decision.At <= TimeSpan.FromHours(24.0)

        match decision.Approvals with
        | [ first; second ] when first.ApproverId <> Guid.Empty && second.ApproverId <> Guid.Empty ->
            if not (bound first && bound second) then
                Error LifecycleRefusal.ApprovalMismatch
            elif first.ApproverId = second.ApproverId then
                Error LifecycleRefusal.ApprovalRequired
            elif first.ExpiresAt <= decision.At || second.ExpiresAt <= decision.At then
                Error LifecycleRefusal.ApprovalExpired
            elif not (validOwnerTime first && validOwnerTime second) then
                Error LifecycleRefusal.ApprovalMismatch
            else
                Ok()
        | _ -> Error LifecycleRefusal.ApprovalRequired
