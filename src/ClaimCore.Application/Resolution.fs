namespace ClaimCore.Application

open System
open ClaimCore.Contract
open ClaimCore.Domain

/// Authorship permits receipt metadata; current authority still controls snapshot disclosure.
module internal Resolution =
    let resolve installationId principal operationId (view: OperationReadView) =
        if not (ActorAuthorization.validActor principal view.Actor) then
            ResolveReply.Rejected Refusal.ResourceUnavailable
        else
            match view.Entry with
            | Some entry when entry.OperationId <> operationId ->
                ResolveReply.Failed CoreFault.StoreIntegrityError
            | Some entry when entry.ActorId = view.Actor.Actor.Value.ActorId ->
                LedgerEntries.validate installationId entry
                |> Result.bind (LedgerEntries.receipt principal view.Actor view.Case)
                |> Result.map ResolveReply.Accepted
                |> Result.defaultWith ResolveReply.Failed
            | _ -> ResolveReply.NotAccepted

    let run (store: ILedgerStore) installationId principal (request: ResolveRequest) token =
        task {
            if request.OperationId = Guid.Empty then
                return
                    ResolveReply.Rejected(
                        Refusal.InvalidInput(InputTarget.OperationId, Violation.OperationIdRequired)
                    )
            else
                let! result =
                    store.ResolveOperation(
                        principal,
                        request.OperationId,
                        resolve installationId principal request.OperationId,
                        token
                    )

                return
                    result
                    |> Result.defaultWith (fun failure ->
                        ResolveReply.Failed(
                            match failure with
                            | StoreFailure.Cancelled
                            | StoreFailure.CommitOutcomeUnknown -> CoreFault.StoreUnavailable
                            | value -> Queries.fault value
                        ))
        }
