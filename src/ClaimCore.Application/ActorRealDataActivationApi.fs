namespace ClaimCore.Application

open System
open System.Threading

/// Bootstrap-safe actor calls never expose the owner activation port or its private proofs.
module internal ActorRealDataActivationApi =
    let approve
        (gate: IActorGate)
        (store: IRealDataActivationApprovalStore)
        principal
        (request: RealDataActivationApprovalRequest)
        (ct: CancellationToken)
        =
        task {
            try
                match!
                    gate.Installation(principal, EndpointAction.ApproveRealDataActivation, ct)
                with
                | Some context -> return! store.Approve(context, request, ct)
                | None -> return RealDataActivationApprovalOutcome.ResourceUnavailable
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return RealDataActivationApprovalOutcome.ResourceUnavailable
            | _ -> return RealDataActivationApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }

    let review
        (gate: IActorGate)
        (store: IRealDataActivationApprovalStore)
        principal
        planId
        (ct: CancellationToken)
        =
        task {
            try
                match!
                    gate.Installation(principal, EndpointAction.ReviewRealDataActivation, ct)
                with
                | Some context -> return! store.Review(context, planId, ct)
                | None -> return RealDataActivationPlanReviewOutcome.ResourceUnavailable
            with _ ->
                return RealDataActivationPlanReviewOutcome.ResourceUnavailable
        }
