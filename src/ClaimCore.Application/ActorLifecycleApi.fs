namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks

/// A case-scoped lifecycle capability; no actor receives the technical store or witness handle.
type ICaseLifecycleWorkflow =
    abstract Review:
        caseReference: string * cancellationToken: CancellationToken -> Task<LifecycleReviewOutcome>

    abstract Apply:
        change: LifecycleChange * cancellationToken: CancellationToken ->
            Task<LifecycleWriteOutcome>

    abstract Approve:
        change: LifecycleChange *
        approvalId: Guid *
        expiresAt: DateTimeOffset *
        cancellationToken: CancellationToken ->
            Task<LifecycleWriteOutcome>

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type private LifecycleAdmission =
    | Available of ActorCallContext
    | Unavailable
    | Cancelled
    | Failed

module internal ActorLifecycleApi =
    let private admit (gate: IActorGate) principal action reference (ct: CancellationToken) =
        task {
            try
                let! context = gate.Case(principal, action, reference, ct)

                return
                    context
                    |> Option.map LifecycleAdmission.Available
                    |> Option.defaultValue LifecycleAdmission.Unavailable
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return LifecycleAdmission.Cancelled
            | _ -> return LifecycleAdmission.Failed
        }

    let private action =
        function
        | LifecycleMutation.VoidDataEntryError _ -> EndpointAction.VoidCase
        | LifecycleMutation.ReinstateVoided _ -> EndpointAction.ReinstateCase
        | LifecycleMutation.RequestErasure _ -> EndpointAction.RequestErasure
        | LifecycleMutation.MarkErasurePending _ -> EndpointAction.RequestErasure
        | LifecycleMutation.PurgeLivePayload _ -> EndpointAction.ApproveErasure
        | LifecycleMutation.RecordHold _
        | LifecycleMutation.ReleaseHold _ -> EndpointAction.ManageHolds

    let private review gate (store: ICaseLifecycleStore) principal reference ct =
        task {
            let! admission = admit gate principal EndpointAction.ReviewLifecycle reference ct

            match admission with
            | LifecycleAdmission.Available context -> return! store.Review(context, reference)
            | LifecycleAdmission.Unavailable -> return LifecycleReviewOutcome.ResourceUnavailable
            | LifecycleAdmission.Cancelled -> return LifecycleReviewOutcome.Cancelled
            | LifecycleAdmission.Failed ->
                return LifecycleReviewOutcome.Failed CoreFault.StoreUnavailable
        }

    let private apply
        gate
        (store: ICaseLifecycleStore)
        (clock: IBusinessTime)
        principal
        (change: LifecycleChange)
        ct
        =
        task {
            if
                match change.Action with
                | LifecycleMutation.PurgeLivePayload _ -> true
                | _ -> false
            then
                return
                    LifecycleWriteOutcome.Refused
                        ClaimCore.Domain.LifecycleRefusal.WrongPrivacyPhase
            else
                let! admission = admit gate principal (action change.Action) change.CaseReference ct

                match admission with
                | LifecycleAdmission.Available context ->
                    let instant = clock.Capture().ObservedUtcInstant
                    return! store.Apply(context, change, instant)
                | LifecycleAdmission.Unavailable -> return LifecycleWriteOutcome.ResourceUnavailable
                | LifecycleAdmission.Cancelled ->
                    return LifecycleWriteOutcome.CancelledBeforeAdmission change.EventId
                | LifecycleAdmission.Failed ->
                    return LifecycleWriteOutcome.Failed CoreFault.StoreUnavailable
        }

    let private approve
        gate
        (store: ICaseLifecycleStore)
        (clock: IBusinessTime)
        principal
        (change: LifecycleChange)
        approvalId
        expiresAt
        ct
        =
        task {
            let approvalAction =
                match change.Action with
                | LifecycleMutation.PurgeLivePayload _ -> EndpointAction.ApproveErasure
                | _ -> EndpointAction.ApproveLifecycle

            let! admission = admit gate principal approvalAction change.CaseReference ct

            match admission with
            | LifecycleAdmission.Available context ->
                let instant = clock.Capture().ObservedUtcInstant
                return! store.Approve(context, change, approvalId, expiresAt, instant)
            | LifecycleAdmission.Unavailable -> return LifecycleWriteOutcome.ResourceUnavailable
            | LifecycleAdmission.Cancelled ->
                return LifecycleWriteOutcome.CancelledBeforeAdmission approvalId
            | LifecycleAdmission.Failed ->
                return LifecycleWriteOutcome.Failed CoreFault.StoreUnavailable
        }

    let create gate store clock principal : ICaseLifecycleWorkflow =
        { new ICaseLifecycleWorkflow with
            member _.Review(reference, ct) =
                review gate store principal reference ct

            member _.Apply(change, ct) =
                apply gate store clock principal change ct

            member _.Approve(change, approvalId, expiresAt, ct) =
                approve gate store clock principal change approvalId expiresAt ct
        }
