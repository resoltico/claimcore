namespace ClaimCore.Application


open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain

module internal Core =
    let private mutationResult =
        function
        | Verdict.Refuse error -> ExecuteResult.Rejected error
        | Verdict.Fail fault -> ExecuteResult.Failed fault
        | Verdict.Proceed receipt -> ExecuteResult.Accepted receipt
        | Verdict.Replay receipt -> ExecuteResult.AlreadyAccepted receipt

    let private mutationFailure =
        function
        | StoreFailure.Cancelled -> ExecuteResult.Failed CoreFault.StoreUnavailable
        | StoreFailure.CommitOutcomeUnknown -> ExecuteResult.OutcomeUnknown
        | failure -> ExecuteResult.Failed(Queries.fault failure)

    let private admitCase request = Case.parse request

    let private admitAuthority = Authority.parse

    let private write
        (ct: CancellationToken)
        admit
        (persist: 'request -> Task<Result<Verdict<OperationReceipt>, StoreFailure>>)
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

    let private caseWrite store installationId calendar principal request ct =
        write ct (fun () -> admitCase request) (fun command ->
            CaseExecution.run store installationId calendar principal command ct)

    let private authorityWrite store installationId principal input ct =
        write ct (fun () -> admitAuthority input) (fun request ->
            AuthorityExecution.run store installationId principal request ct)

    let private businessFields =
        function
        | None -> Map.empty
        | Some {
                   State = (CaseState.Active(fields) | CaseState.Voided(fields))
               } -> BusinessFields.values fields |> Map.ofList
        | Some { State = CaseState.Erased } -> Map.empty

    let private changes act before after =
        match act with
        | CaseAct.Void _
        | CaseAct.Reinstate _
        | CaseAct.Erase -> []
        | _ ->
            let previous = businessFields before
            let next = businessFields (Some after)

            Record.fields
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

    let private advisory productVersion (calendar: IBusinessCalendar) instant act before after =
        let before = before |> Option.map Case.snapshot
        let after = Case.snapshot after

        {
            Before = before
            Proposed = after
            Changes = changes act before after
            Context =
                {
                    ProductVersion = productVersion
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
                        | Verdict.Refuse refusal -> return PreviewResult.Rejected refusal
                        | Verdict.Fail fault -> return PreviewResult.Failed fault
                        | Verdict.Replay receipt -> return PreviewResult.AlreadyAccepted receipt
                        | Verdict.Proceed proposal ->
                            return PreviewResult.Reviewed(render request view proposal)
        }

    let private preview
        productVersion
        (store: IStore)
        installationId
        calendar
        principal
        request
        ct
        =
        review
            ct
            (fun () -> admitCase request)
            (fun (admitted: CaseCommand) ->
                store.Read(
                    (fun tx ->
                        ExecutionFacts.case
                            tx
                            installationId
                            principal
                            (Some admitted.OperationId)
                            (CaseReference.format admitted.Reference)),
                    ct
                ))
            (CaseExecution.review installationId calendar principal)
            (fun admitted (view: CaseFacts) (before, after) ->
                advisory productVersion calendar view.ObservedAt admitted.Act before after
                |> OperationReview.Case)

    let private authorityPreview (store: IStore) installationId principal input ct =
        review
            ct
            (fun () -> admitAuthority input)
            (fun (admitted: AuthorityCommand) ->
                store.Read(
                    (fun tx ->
                        ExecutionFacts.target
                            tx
                            installationId
                            principal
                            admitted.OperationId
                            admitted.Target
                            (AuthorityRequests.targetCase admitted)),
                    ct
                ))
            (AuthorityExecution.review installationId principal)
            (fun _ (view: AuthorityExecutionFacts) (_, projection) ->
                AuthorityReviews.describe view projection |> OperationReview.Authority)

    let private previewOperation productVersion store installationId calendar principal input ct =
        task {
            let! outcome =
                match input with
                | OperationInput.Case request ->
                    preview productVersion store installationId calendar principal request ct
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

    let context (services: ServiceContext) () ct =
        Queries.context
            services.ProductVersion
            services.Store
            services.Calendar
            services.Principal
            ct

    let previewReply (services: ServiceContext) input ct =
        previewOperation
            services.ProductVersion
            services.Store
            services.InstallationId
            services.Calendar
            services.Principal
            input
            ct

    let execute services input ct =
        task {
            let! outcome =
                acceptOperation
                    services.Store
                    services.InstallationId
                    services.Calendar
                    services.Principal
                    input
                    ct

            return
                {
                    ExecuteReply.OperationId = OperationInput.operationId input
                    Outcome = outcome
                }
        }

    let resolve services input ct =
        Resolution.run services.Store services.InstallationId services.Principal input ct

    let get services (input: CaseGetRequest) ct =
        Queries.get services.Store services.InstallationId services.Principal input.CaseReference ct

    let list services input ct =
        Queries.list services.Store services.InstallationId services.Principal input ct

    let history services input ct =
        Queries.history services.Store services.InstallationId services.Principal input ct

    let observe services (input: ObserveRequest) ct =
        Queries.observe
            services.Store
            services.InstallationId
            services.Principal
            input.OperationId
            ct

    let grants services input ct =
        Queries.grants services.Store services.Principal input ct

    let actors services input ct =
        Queries.actors services.Store services.Principal input ct
