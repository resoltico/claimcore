namespace ClaimCore.Application

open System
open System.Threading

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type private TombstoneAdmission =
    | Available of ActorCallContext
    | Unavailable
    | Cancelled
    | Failed

module internal ActorTombstoneApi =
    let private admit (gate: IActorGate) principal action caseId (ct: CancellationToken) =
        task {
            try
                let! context = gate.Tombstone(principal, action, caseId, ct)

                return
                    context
                    |> Option.map TombstoneAdmission.Available
                    |> Option.defaultValue TombstoneAdmission.Unavailable
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return TombstoneAdmission.Cancelled
            | _ -> return TombstoneAdmission.Failed
        }

    let private review gate (store: ITombstoneStore) principal caseId ct =
        task {
            let! admission = admit gate principal EndpointAction.ReviewTombstone caseId ct

            match admission with
            | TombstoneAdmission.Available context -> return! store.Review(context, caseId)
            | TombstoneAdmission.Unavailable -> return TombstoneReviewOutcome.ResourceUnavailable
            | TombstoneAdmission.Cancelled -> return TombstoneReviewOutcome.Cancelled
            | TombstoneAdmission.Failed ->
                return TombstoneReviewOutcome.Failed CoreFault.StoreUnavailable
        }

    let private approve
        gate
        (store: ITombstoneStore)
        (clock: IBusinessTime)
        principal
        (proposal: TombstonePruneProposal)
        approvalId
        expiresAt
        ct
        =
        task {
            let! admission =
                admit gate principal EndpointAction.ApproveWitnessPrune proposal.CaseId ct

            match admission with
            | TombstoneAdmission.Available context ->
                let instant = clock.Capture().ObservedUtcInstant
                return! store.ApproveWitnessPrune(context, proposal, approvalId, expiresAt, instant)
            | TombstoneAdmission.Unavailable -> return TombstoneWriteOutcome.ResourceUnavailable
            | TombstoneAdmission.Cancelled ->
                return TombstoneWriteOutcome.CancelledBeforeAdmission approvalId
            | TombstoneAdmission.Failed ->
                return TombstoneWriteOutcome.Failed CoreFault.StoreUnavailable
        }

    let private changeHold
        gate
        (store: ITombstoneStore)
        (clock: IBusinessTime)
        principal
        (change: TombstoneHoldChange)
        ct
        =
        task {
            let! admission =
                admit gate principal EndpointAction.ManageTombstoneHold change.CaseId ct

            match admission with
            | TombstoneAdmission.Available context ->
                let instant = clock.Capture().ObservedUtcInstant
                return! store.ChangeHold(context, change, instant)
            | TombstoneAdmission.Unavailable -> return TombstoneWriteOutcome.ResourceUnavailable
            | TombstoneAdmission.Cancelled ->
                return TombstoneWriteOutcome.CancelledBeforeAdmission change.EventId
            | TombstoneAdmission.Failed ->
                return TombstoneWriteOutcome.Failed CoreFault.StoreUnavailable
        }

    let private approveTerminal
        gate
        (store: ITombstoneStore)
        (clock: IBusinessTime)
        principal
        proposal
        approvalId
        expiresAt
        ct
        =
        task {
            let caseId = TombstoneTerminalProposal.caseId proposal

            let! admission = admit gate principal EndpointAction.ApproveTerminalErasure caseId ct

            match admission with
            | TombstoneAdmission.Available context ->
                let instant = clock.Capture().ObservedUtcInstant
                return! store.ApproveTerminal(context, proposal, approvalId, expiresAt, instant)
            | TombstoneAdmission.Unavailable -> return TombstoneWriteOutcome.ResourceUnavailable
            | TombstoneAdmission.Cancelled ->
                return TombstoneWriteOutcome.CancelledBeforeAdmission approvalId
            | TombstoneAdmission.Failed ->
                return TombstoneWriteOutcome.Failed CoreFault.StoreUnavailable
        }

    let create gate (store: ITombstoneStore) (clock: IBusinessTime) principal : ITombstoneWorkflow =
        { new ITombstoneWorkflow with
            member _.Review(caseId, ct) = review gate store principal caseId ct

            member _.ApproveWitnessPrune(proposal, approvalId, expiresAt, ct) =
                approve gate store clock principal proposal approvalId expiresAt ct

            member _.ApproveTerminal(proposal, approvalId, expiresAt, ct) =
                approveTerminal gate store clock principal proposal approvalId expiresAt ct

            member _.ChangeHold(change, ct) =
                changeHold gate store clock principal change ct
        }
