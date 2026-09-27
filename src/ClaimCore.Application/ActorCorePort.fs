namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

/// One explicitly scoped acquisition result. Command calls carry the reserved opaque case ID;
/// other calls still bind the principal and observed grant revision for the storage recheck.
[<NoEquality; NoComparison>]
type internal ActorCallContext =
    {
        Binding: ActorBinding
        CaseId: Guid option
        Action: EndpointAction
        Suppression: ISuppressionCommitments
    }

type internal ICopySignerApprovalStore =
    abstract Approve:
        actor: ActorCallContext *
        request: CopySignerApprovalRequest *
        cancellationToken: CancellationToken ->
            Task<CopySignerApprovalOutcome>

type internal ICopyDeletionApprovalStore =
    abstract Approve:
        actor: ActorCallContext *
        request: CopyDeletionApprovalRequest *
        cancellationToken: CancellationToken ->
            Task<CopyDeletionApprovalOutcome>

type internal ICopyAdoptionApprovalStore =
    abstract Approve:
        actor: ActorCallContext *
        request: CopyAdoptionApprovalRequest *
        cancellationToken: CancellationToken ->
            Task<CopyAdoptionApprovalOutcome>

type internal IWriterHandoffApprovalStore =
    abstract Approve:
        actor: ActorCallContext *
        request: WriterHandoffApprovalRequest *
        cancellationToken: CancellationToken ->
            Task<WriterHandoffApprovalOutcome>

type internal IRealDataActivationApprovalStore =
    abstract Review:
        actor: ActorCallContext * planId: Guid * cancellationToken: CancellationToken ->
            Task<RealDataActivationPlanReviewOutcome>

    abstract Approve:
        actor: ActorCallContext *
        request: RealDataActivationApprovalRequest *
        cancellationToken: CancellationToken ->
            Task<RealDataActivationApprovalOutcome>

/// Storage resolves references and operation IDs without asking a caller to supply a case ID.
/// Application chooses the semantic endpoint action and interprets the returned authority.
type internal IActorGate =
    abstract Installation:
        principal: PrincipalKey * action: EndpointAction * cancellationToken: CancellationToken ->
            Task<ActorCallContext option>

    abstract Definition:
        principal: PrincipalKey * cancellationToken: CancellationToken ->
            Task<ActorCallContext option>

    abstract Command:
        principal: PrincipalKey *
        action: EndpointAction *
        request: CommandRequest *
        cancellationToken: CancellationToken ->
            Task<ActorCallContext option>

    abstract Case:
        principal: PrincipalKey *
        action: EndpointAction *
        reference: string *
        cancellationToken: CancellationToken ->
            Task<ActorCallContext option>

    /// The opaque ID must resolve to a live-purged tombstone before case-scoped authority is
    /// considered; nonexistent and inaccessible handles share one refusal.
    abstract Tombstone:
        principal: PrincipalKey *
        action: EndpointAction *
        caseId: Guid *
        cancellationToken: CancellationToken ->
            Task<ActorCallContext option>

    /// The case ID/reference pair comes only from an authenticated v3 preview of the same bytes.
    abstract Import:
        principal: PrincipalKey *
        caseId: Guid *
        operationId: Guid *
        reference: string *
        cancellationToken: CancellationToken ->
            Task<ActorCallContext option>

    abstract Operation:
        principal: PrincipalKey *
        action: EndpointAction *
        operationId: Guid *
        cancellationToken: CancellationToken ->
            Task<ActorCallContext option>

    abstract List:
        principal: PrincipalKey * cancellationToken: CancellationToken ->
            Task<ActorCallContext option>
