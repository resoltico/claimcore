namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type private GateResult =
    | Admitted of ActorCallContext
    | Unavailable
    | Cancelled
    | Failed

/// Application chooses every endpoint capability before a scoped core is composed.
module internal ActorCoreApi =
    let private acquire (operation: Task<ActorCallContext option>) (ct: CancellationToken) =
        task {
            try
                let! result = operation

                return
                    result
                    |> Option.map GateResult.Admitted
                    |> Option.defaultValue GateResult.Unavailable
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return GateResult.Cancelled
            | _ -> return GateResult.Failed
        }

    let private prepare
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        (request: CommandRequest)
        (ct: CancellationToken)
        =
        task {
            let action = ActorMutationDisclosure.commandAction true request
            let! result = acquire (gate.Command(principal, action, request, ct)) ct

            match result with
            | GateResult.Admitted context -> return! (factory context).Prepare(request, ct)
            | GateResult.Unavailable ->
                return
                    PrepareOutcome.PrepareRejected(
                        request.OperationId,
                        Rejection.ResourceUnavailable
                    )
            | GateResult.Cancelled ->
                return PrepareOutcome.CancelledBeforeAdmission request.OperationId
            | GateResult.Failed ->
                return PrepareOutcome.PrepareFailed(request.OperationId, CoreFault.StoreUnavailable)
        }

    let private execute
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        (request: CommandRequest)
        (ct: CancellationToken)
        =
        task {
            let action = ActorMutationDisclosure.commandAction false request
            let! result = acquire (gate.Command(principal, action, request, ct)) ct

            match result with
            | GateResult.Admitted context -> return! (factory context).Execute(request, ct)
            | GateResult.Unavailable ->
                return SubmissionOutcome.RejectedBeforeAttempt(None, Rejection.ResourceUnavailable)
            | GateResult.Cancelled ->
                return SubmissionOutcome.CancelledBeforeAdmission request.OperationId
            | GateResult.Failed ->
                return SubmissionOutcome.FailedBeforeAttempt(None, CoreFault.StoreUnavailable)
        }

    let private caseQuery
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        action
        reference
        (invoke: IClaimsCore -> Task<QueryOutcome<'value>>)
        (ct: CancellationToken)
        =
        task {
            let! result = acquire (gate.Case(principal, action, reference, ct)) ct

            match result with
            | GateResult.Admitted context -> return! invoke (factory context)
            | GateResult.Unavailable -> return QueryOutcome.Rejected Rejection.ResourceUnavailable
            | GateResult.Cancelled -> return QueryOutcome.Cancelled
            | GateResult.Failed -> return QueryOutcome.Failed CoreFault.StoreUnavailable
        }

    let private get gate factory principal reference ct =
        caseQuery
            gate
            factory
            principal
            EndpointAction.GetCase
            reference
            (fun core -> core.Get(reference, ct))
            ct

    let private history gate factory principal request ct =
        let action =
            match request.Detail with
            | HistoryDetail.Summary -> EndpointAction.HistorySummary
            | HistoryDetail.Full -> EndpointAction.HistoryFull

        caseQuery
            gate
            factory
            principal
            action
            request.CaseReference
            (fun core -> core.History(request, ct))
            ct

    let private operationQuery
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        (operationId: Guid)
        (ct: CancellationToken)
        =
        task {
            let! result =
                acquire
                    (gate.Operation(principal, EndpointAction.ObserveOperation, operationId, ct))
                    ct

            match result with
            | GateResult.Admitted context ->
                return! (factory context).ObserveOperation(operationId, ct)
            | GateResult.Unavailable -> return QueryOutcome.Rejected Rejection.ResourceUnavailable
            | GateResult.Cancelled -> return QueryOutcome.Cancelled
            | GateResult.Failed -> return QueryOutcome.Failed CoreFault.StoreUnavailable
        }

    let private definition
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        principal
        ct
        =
        task {
            let! result = acquire (gate.Definition(principal, ct)) ct

            match result with
            | GateResult.Admitted context ->
                return QueryOutcome.Succeeded((factory context).Describe())
            | GateResult.Unavailable -> return QueryOutcome.Rejected Rejection.ResourceUnavailable
            | GateResult.Cancelled -> return QueryOutcome.Cancelled
            | GateResult.Failed -> return QueryOutcome.Failed CoreFault.StoreUnavailable
        }

    let private list
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        principal
        request
        ct
        =
        task {
            let! result = acquire (gate.List(principal, ct)) ct

            match result with
            | GateResult.Admitted context -> return! (factory context).List(request, ct)
            | GateResult.Unavailable -> return QueryOutcome.Rejected Rejection.ResourceUnavailable
            | GateResult.Cancelled -> return QueryOutcome.Cancelled
            | GateResult.Failed -> return QueryOutcome.Failed CoreFault.StoreUnavailable
        }

    let private approveSigner
        (gate: IActorGate)
        (store: ICopySignerApprovalStore)
        principal
        (request: CopySignerApprovalRequest)
        ct
        =
        task {
            let! admission =
                acquire (gate.Installation(principal, EndpointAction.ApproveCopySigner, ct)) ct

            match admission with
            | GateResult.Admitted context -> return! store.Approve(context, request, ct)
            | GateResult.Unavailable
            | GateResult.Cancelled -> return CopySignerApprovalOutcome.ResourceUnavailable
            | GateResult.Failed ->
                return CopySignerApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }

    let private approveDeletion
        (gate: IActorGate)
        (store: ICopyDeletionApprovalStore)
        principal
        (request: CopyDeletionApprovalRequest)
        ct
        =
        task {
            let! admission =
                acquire (gate.Installation(principal, EndpointAction.ApproveCopyDeletion, ct)) ct

            match admission with
            | GateResult.Admitted context -> return! store.Approve(context, request, ct)
            | GateResult.Unavailable
            | GateResult.Cancelled -> return CopyDeletionApprovalOutcome.ResourceUnavailable
            | GateResult.Failed ->
                return CopyDeletionApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }

    let private approveHandoff
        (gate: IActorGate)
        (store: IWriterHandoffApprovalStore)
        principal
        (request: WriterHandoffApprovalRequest)
        ct
        =
        task {
            let! admission =
                acquire (gate.Installation(principal, EndpointAction.ApproveWriterHandoff, ct)) ct

            match admission with
            | GateResult.Admitted context -> return! store.Approve(context, request, ct)
            | GateResult.Unavailable
            | GateResult.Cancelled -> return WriterHandoffApprovalOutcome.ResourceUnavailable
            | GateResult.Failed ->
                return WriterHandoffApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }

    let create
        (gate: IActorGate)
        (factory: ActorCallContext -> IClaimsCore)
        (principal: PrincipalKey)
        (management: IActorManagement)
        (lifecycle: ICaseLifecycleWorkflow)
        (tombstones: ITombstoneWorkflow)
        (signerApproval: ICopySignerApprovalStore)
        (deletionApproval: ICopyDeletionApprovalStore)
        (adoptionApproval: ICopyAdoptionApprovalStore)
        (handoffApproval: IWriterHandoffApprovalStore)
        (realDataActivationApproval: IRealDataActivationApprovalStore)
        : IActorClaimsCore =
        let approveActivation =
            ActorRealDataActivationApi.approve gate realDataActivationApproval principal

        let reviewActivation =
            ActorRealDataActivationApi.review gate realDataActivationApproval principal

        { new IActorClaimsCore with
            member _.Definition(ct) = definition gate factory principal ct

            member _.Prepare(request, ct) =
                prepare gate factory principal request ct

            member _.Execute(request, ct) =
                execute gate factory principal request ct

            member _.Get(reference, ct) = get gate factory principal reference ct

            member _.List(request, ct) = list gate factory principal request ct

            member _.History(request, ct) =
                history gate factory principal request ct

            member _.ObserveOperation(operationId, ct) =
                operationQuery gate factory principal operationId ct

            member _.Recovery = ActorRecoveryApi.create gate factory principal
            member _.Management = management
            member _.Lifecycle = lifecycle
            member _.Tombstones = tombstones

            member _.ApproveCopySigner(request, ct) =
                approveSigner gate signerApproval principal request ct

            member _.ApproveCopyDeletion(request, ct) =
                approveDeletion gate deletionApproval principal request ct

            member _.ApproveCopyAdoption(request, ct) =
                ActorCopyAdoptionApi.approve gate adoptionApproval principal request ct

            member _.ApproveWriterHandoff(request, ct) =
                approveHandoff gate handoffApproval principal request ct

            member _.ApproveRealDataActivation(request, ct) = approveActivation request ct

            member _.ReviewRealDataActivation(planId, ct) = reviewActivation planId ct
        }
