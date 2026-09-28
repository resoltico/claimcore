namespace ClaimCore.Application

open System
open System.Threading

/// Tombstone-scoped HUMAN OWNER draft approval remains actor-bound; no caller actor ID or
/// schema-owner authority is supplied through the transport request.
module internal ActorCopyAdoptionApi =
    let approve
        (gate: IActorGate)
        (store: ICopyAdoptionApprovalStore)
        principal
        (request: CopyAdoptionApprovalRequest)
        (ct: CancellationToken)
        =
        task {
            try
                let! context =
                    gate.Tombstone(
                        principal,
                        EndpointAction.ApproveCopyAdoption,
                        request.CaseId,
                        ct
                    )

                match context with
                | Some actor -> return! store.Approve(actor, request, ct)
                | None -> return CopyAdoptionApprovalOutcome.ResourceUnavailable
            with
            | :? OperationCanceledException when ct.IsCancellationRequested ->
                return CopyAdoptionApprovalOutcome.ResourceUnavailable
            | _ -> return CopyAdoptionApprovalOutcome.StartedUnconfirmed request.ApprovalId
        }
