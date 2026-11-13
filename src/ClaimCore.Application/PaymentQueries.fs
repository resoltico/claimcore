namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal PaymentQueries =
    let page (services: ServiceContext) (request: CasePaymentsRequest) token =
        task {
            if request.Limit < 1 || request.Limit > PaymentPaging.MaximumLimit then
                return!
                    RequestRefusals.read
                        services
                        token
                        (Refusal.invalidInput (
                            InputTarget.Limit,
                            Violation.PageLimitRange PaymentPaging.MaximumLimit
                        ))
            else
                match CaseExecution.validateSelector request.Target with
                | Error refusal -> return! RequestRefusals.read services token refusal
                | Ok() ->
                    let! result =
                        services.Store.Read
                            {
                                CancellationToken = token
                                Work =
                                    fun tx ->
                                        task {
                                            let! view =
                                                ExecutionFacts.case
                                                    services
                                                    tx
                                                    None
                                                    request.Target
                                                    None

                                            match view.Case with
                                            | Some parent when
                                                Access.can
                                                    services.Principal
                                                    view.Actor
                                                    Capability.ReadCase
                                                    (Scope.Case parent.CaseId)
                                                ->
                                                match
                                                    QueryResults.cursor
                                                        (Cursors.decodePayments
                                                            services.InstallationId
                                                            parent.CaseId)
                                                        request.AfterCursor
                                                with
                                                | Error refusal -> return Outcome.Rejected refusal
                                                | Ok before ->
                                                    let! rows =
                                                        match parent.Snapshot.State with
                                                        | CaseState.Erased ->
                                                            System.Threading.Tasks.Task.FromResult(
                                                                { Items = []; Next = None }
                                                                : Page<
                                                                    PaymentEntry * EffectPosition,
                                                                    EffectPosition
                                                                   >
                                                            )
                                                        | _ ->
                                                            tx.PaymentPage
                                                                {
                                                                    CaseId = parent.CaseId
                                                                    Before = before
                                                                    Limit = request.Limit
                                                                }

                                                    let canRemove =
                                                        not view.Maintenance.Active
                                                        && Access.canAct
                                                            services.Principal
                                                            view.Actor
                                                            (Scope.Case parent.CaseId)
                                                            (CaseAct.probe CaseActKind.RemovePayment)
                                                        && (Case.ofAccepted parent.Snapshot
                                                            |> Result.map (
                                                                Case.actRefusal
                                                                    CaseActKind.RemovePayment
                                                            )
                                                                =
                                                                Ok None)

                                                    return
                                                        Outcome.Succeeded
                                                            {
                                                                Basis =
                                                                    ServiceContext.basis
                                                                        services
                                                                        tx.ObservedAt
                                                                CaseId = parent.CaseId
                                                                Reference =
                                                                    parent.Snapshot.Reference
                                                                Revision = parent.Snapshot.Revision
                                                                State =
                                                                    CaseState.map
                                                                        _.Finance
                                                                        parent.Snapshot.State
                                                                Items =
                                                                    rows.Items
                                                                    |> List.map
                                                                        (fun (entry, position) ->
                                                                            {
                                                                                Entry = entry
                                                                                Position = position
                                                                                CanRemove =
                                                                                    canRemove
                                                                            })
                                                                NextCursor =
                                                                    rows.Next
                                                                    |> Option.map (
                                                                        Cursors.encodePayments
                                                                            services.InstallationId
                                                                            parent.CaseId
                                                                    )
                                                            }
                                            | _ -> return QueryResults.unavailable
                                        }
                            }

                    return QueryResults.readResult result
        }
