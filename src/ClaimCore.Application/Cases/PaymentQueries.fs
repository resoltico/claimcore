namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
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
                match CaseRecords.validateSelector request.Target with
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
                                                CaseReadContext.load services tx request.Target

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
                                                    let! work = WorkReads.findWork tx parent.CaseId

                                                    match work with
                                                    | Some work when
                                                        work.CaseId = parent.CaseId
                                                        && (match
                                                                parent.Snapshot.State, work.Snapshot
                                                            with
                                                            | CaseState.Erased, None -> true
                                                            | _, Some snapshot ->
                                                                snapshot.Revision = work.Revision
                                                            | _ -> false)
                                                        ->
                                                        let! rows =
                                                            match parent.Snapshot.State with
                                                            | CaseState.Erased ->
                                                                System.Threading.Tasks
                                                                    .Task.FromResult(
                                                                        { Items = []; Next = None }
                                                                        : Page<
                                                                            PaymentEntry *
                                                                            EffectPosition,
                                                                            EffectPosition
                                                                           >
                                                                    )
                                                            | _ ->
                                                                PaymentReads.paymentPage
                                                                    tx
                                                                    {
                                                                        CaseId = parent.CaseId
                                                                        Before = before
                                                                        Limit = request.Limit
                                                                    }

                                                        let! family =
                                                            AggregateStorage.family tx parent.CaseId

                                                        let removable =
                                                            ClaimCore.Domain.Tasks.Registry.all
                                                            |> List.tryPick (function
                                                                | ClaimCore.Domain.Tasks.Task.Family task when
                                                                    task.Declaration.Inputs
                                                                    |> List.exists (fun input ->
                                                                        input.Input =
                                                                            TaskInputId.Target
                                                                                InputTarget
                                                                                    .PaymentId
                                                                        && input.Source =
                                                                            InputSource.Binding)
                                                                    ->
                                                                    Some task
                                                                | _ -> None)

                                                        let canRemove =
                                                            match family, removable with
                                                            | Some(family, _, _), Some task ->
                                                                let environment =
                                                                    {
                                                                        FamilyEnvironment.Requester =
                                                                            view.Actor
                                                                        RequesterPrincipal =
                                                                            services.Principal
                                                                        Maintenance =
                                                                            view.Maintenance.Active
                                                                        AssigneeEligible =
                                                                            fun _ -> false
                                                                    }

                                                                (task.Availability
                                                                    environment
                                                                    (Some family)
                                                                    (Binding.Case(
                                                                        CaseSelector.Id family.Id,
                                                                        family.Case.Revision
                                                                    )))
                                                                    .Blocked.IsNone
                                                            | _ -> false

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
                                                                    Revision =
                                                                        parent.Snapshot.Revision
                                                                    WorkRevision = work.Revision
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
                                                                                    Position =
                                                                                        position
                                                                                    CanRemove =
                                                                                        canRemove
                                                                                })
                                                                    NextCursor =
                                                                        rows.Next
                                                                        |> Option.map (
                                                                            Cursors.encodePayments
                                                                                services
                                                                                    .InstallationId
                                                                                parent.CaseId
                                                                        )
                                                                }
                                                    | _ ->
                                                        return
                                                            Outcome.Failed
                                                                CoreFault.StoreIntegrityError
                                            | _ -> return QueryResults.unavailable
                                        }
                            }

                    return QueryResults.readResult result
        }
