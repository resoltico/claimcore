namespace ClaimCore.Application

open ClaimCore.Contract

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

module internal Core =
    let private mutationResult =
        function
        | LedgerWriteResult.Refused error -> ExecuteResult.Rejected error
        | LedgerWriteResult.Failed fault -> ExecuteResult.Failed fault
        | LedgerWriteResult.Appended receipt -> ExecuteResult.Accepted receipt
        | LedgerWriteResult.Replayed receipt -> ExecuteResult.AlreadyAccepted receipt

    let private mutationFailure =
        function
        | StoreFailure.Cancelled -> ExecuteResult.Failed CoreFault.StoreUnavailable
        | StoreFailure.CommitOutcomeUnknown -> ExecuteResult.OutcomeUnknown
        | failure -> ExecuteResult.Failed(Queries.fault failure)

    let private admitCase request =
        Case.validateRequest request |> Result.map (fun () -> request)

    let private admitAuthority input =
        AuthorityRequests.admit input
        |> Result.bind (fun request ->
            AuthorityRequests.validate request |> Result.map (fun () -> request))

    let private write
        (ct: CancellationToken)
        admit
        (persist: 'request -> Task<Result<LedgerWriteResult, StoreFailure>>)
        =
        task {
            if ct.IsCancellationRequested then
                return ExecuteResult.Failed CoreFault.StoreUnavailable
            else
                match admit () with
                | Error refusal -> return ExecuteResult.Rejected refusal
                | Ok request ->
                    let! result = persist request
                    return result |> Result.map mutationResult |> Result.defaultWith mutationFailure
        }

    let private caseWrite (store: ILedgerStore) installationId calendar principal request ct =
        write ct (fun () -> admitCase request) (fun (admitted: CaseRequest) ->
            store.WriteCase(
                principal,
                admitted.OperationId,
                admitted.CaseReference,
                CaseDecisions.execute installationId calendar principal admitted,
                ct
            ))

    let private authorityWrite (store: ILedgerStore) installationId principal input ct =
        write ct (fun () -> admitAuthority input) (fun (admitted: AdmittedAuthorityRequest) ->
            store.WriteAuthority(
                principal,
                admitted.OperationId,
                admitted.Target,
                AuthorityRequests.targetCase admitted,
                AuthorityDecisions.execute installationId principal admitted,
                ct
            ))

    let private businessFields =
        function
        | None -> Map.empty
        | Some { View = CaseView.Live(fields, _) } -> FieldDefinitions.values fields |> Map.ofList
        | Some { View = CaseView.Erased } -> Map.empty

    let private changes act before after =
        match act with
        | CaseAct.Void _
        | CaseAct.Reinstate _
        | CaseAct.Erase -> []
        | _ ->
            let previous = businessFields before
            let next = businessFields (Some after)

            FieldDefinitions.all
            |> List.choose (fun field ->
                let before = Map.tryFind field.Field previous |> Option.flatten
                let after = Map.tryFind field.Field next |> Option.flatten

                if before = after then
                    None
                else
                    Some
                        {
                            FieldName = field.Field
                            Before = before
                            After = after
                        })

    let private advisory (calendar: IBusinessCalendar) instant act before after =
        let before = before |> Option.map Case.snapshot
        let after = Case.snapshot after

        {
            Before = before
            Proposed = after
            Changes = changes act before after
            Context =
                {
                    ProductVersion = BuildIdentity.current.Version
                    EffectiveBusinessDate = calendar.DateAt instant
                    TimeZoneId = calendar.TimeZoneId
                }
        }

    let private review
        (ct: CancellationToken)
        admit
        (read: 'request -> Task<Result<'view, StoreFailure>>)
        decide
        render
        =
        task {
            if ct.IsCancellationRequested then
                return PreviewResult.Failed CoreFault.StoreUnavailable
            else
                match admit () with
                | Error refusal -> return PreviewResult.Rejected refusal
                | Ok request ->
                    let! result = read request

                    match result with
                    | Error StoreFailure.Cancelled ->
                        return PreviewResult.Failed CoreFault.StoreUnavailable
                    | Error failure -> return PreviewResult.Failed(Queries.fault failure)
                    | Ok view ->
                        match decide request view with
                        | LedgerReviewDecision.Refuse refusal ->
                            return PreviewResult.Rejected refusal
                        | LedgerReviewDecision.Fail fault -> return PreviewResult.Failed fault
                        | LedgerReviewDecision.Replay receipt ->
                            return PreviewResult.AlreadyAccepted receipt
                        | LedgerReviewDecision.Proposed proposal ->
                            return PreviewResult.Reviewed(render request view proposal)
        }

    let private preview (store: ILedgerStore) installationId calendar principal request ct =
        review
            ct
            (fun () -> admitCase request)
            (fun (admitted: CaseRequest) ->
                store.ReadCase(principal, Some admitted.OperationId, admitted.CaseReference, ct))
            (CaseDecisions.review installationId calendar principal)
            (fun admitted (view: CaseReadView) (before, after) ->
                advisory calendar view.ObservedAt admitted.Act before after
                |> OperationReview.Case)

    let private authorityPreview (store: ILedgerStore) installationId principal input ct =
        review
            ct
            (fun () -> admitAuthority input)
            (fun (admitted: AdmittedAuthorityRequest) ->
                store.ReadAuthorityTarget(
                    principal,
                    admitted.OperationId,
                    admitted.Target,
                    AuthorityRequests.targetCase admitted,
                    ct
                ))
            (AuthorityDecisions.review installationId principal)
            (fun _ (view: AuthorityReadView) (_, projection) ->
                AuthorityReviews.describe view projection |> OperationReview.Authority)

    let private previewOperation store installationId calendar principal input ct =
        task {
            let! outcome =
                match input with
                | OperationInput.Case request ->
                    preview store installationId calendar principal request ct
                | OperationInput.Authority request ->
                    authorityPreview store installationId principal request ct

            return
                ({
                    OperationId = OperationInput.operationId input
                    Outcome = outcome
                }
                : PreviewReply)
        }

    let private acceptOperation store installationId calendar principal input ct =
        match input with
        | OperationInput.Case request ->
            caseWrite store installationId calendar principal request ct
        | OperationInput.Authority request ->
            authorityWrite store installationId principal request ct

    type private ActorCore
        (installationId, store: ILedgerStore, calendar: IBusinessCalendar, principal) =
        interface IActorClaimsCore with
            member _.Context ct =
                Queries.context store calendar principal ct

            member _.Preview(input, ct) =
                previewOperation store installationId calendar principal input ct

            member _.Execute(input, ct) =
                task {
                    let! outcome = acceptOperation store installationId calendar principal input ct

                    return
                        {
                            OperationId = OperationInput.operationId input
                            Outcome = outcome
                        }
                }

            member _.Resolve(request, ct) =
                Resolution.run store installationId principal request ct

            member _.Get(request, ct) =
                Queries.get store installationId principal request.CaseReference ct

            member _.List(request, ct) =
                Queries.list store installationId principal request ct

            member _.History(request, ct) =
                Queries.history store installationId principal request ct

            member _.Observe(request, ct) =
                Queries.observe store installationId principal request.OperationId ct

            member _.ListGrants(request, ct) =
                Queries.grants store principal request ct

            member _.ListActors(request, ct) =
                Queries.actors store principal request ct

    /// Every facade authorizes each read against current authoritative state.
    let create installationId store calendar principal : IActorClaimsCore =
        if installationId = Guid.Empty then
            invalidArg (nameof installationId) "Use an admitted installation."

        ActorCore(installationId, store, calendar, principal) :> IActorClaimsCore
