namespace ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// The typed application boundary owns orchestration; hosts supply immutable request dependencies.
module Service =
    /// Hosts add their instruments to the single application meter.
    let metrics = ApplicationMetrics.meter

    /// Storage reports only elapsed head-lock time, with no provider or subject data.
    let recordHeadWait elapsed = ApplicationMetrics.waited elapsed

    /// Storage reports elapsed append time without leaking provider or subject data.
    let recordAppend elapsed = ApplicationMetrics.appended elapsed

    let create productVersion installationId store calendar principal =
        if String.IsNullOrWhiteSpace productVersion then
            invalidArg (nameof productVersion) "Supply the host's compiled product version."

        if installationId = Guid.Empty then
            invalidArg (nameof installationId) "Use an admitted installation."

        {
            ProductVersion = productVersion
            InstallationId = installationId
            Store = store
            Calendar = calendar
            Claims = RequesterClaims.empty
            Principal = principal
        }

    /// Hosts may decorate storage with request-scoped resource admission.
    let mapStore transform (services: ServiceContext) =
        { services with
            Store = transform services.Store
        }

    let withClaims claims (services: ServiceContext) = { services with Claims = claims }

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

    let private admitCase = CaseExecution.prepare

    let private admitAuthority = Authority.parse

    let private write
        (services: ServiceContext)
        (ct: CancellationToken)
        admit
        (persist: 'request -> Task<Result<Verdict<OperationReceipt>, StoreFailure>>)
        =
        task {
            if ct.IsCancellationRequested then
                return ExecuteResult.Failed CoreFault.StoreUnavailable
            else
                match admit () with
                | Error refusal ->
                    let! result = RequestRefusals.write services ct refusal
                    return result |> Result.map mutationResult |> Result.defaultWith mutationFailure
                | Ok request ->
                    let! result = persist request
                    return result |> Result.map mutationResult |> Result.defaultWith mutationFailure
        }

    let private advisory (services: ServiceContext) instant act before after =
        let before = before |> Option.map Case.snapshot
        let after = Case.snapshot after

        {
            Before = before
            Proposed = after
            Changes = CaseChanges.review act before after
            Context = ServiceContext.runtime services instant
        }

    let private review
        (services: ServiceContext)
        (ct: CancellationToken)
        admit
        (read: 'request -> Task<Result<'view, StoreFailure>>)
        decide
        render
        =
        task {
            if ct.IsCancellationRequested then
                return Outcome.Failed CoreFault.StoreUnavailable
            else
                match admit () with
                | Error refusal -> return! RequestRefusals.read services ct refusal
                | Ok request ->
                    let! result = read request

                    match result with
                    | Error StoreFailure.Cancelled ->
                        return Outcome.Failed CoreFault.StoreUnavailable
                    | Error failure -> return Queries.failure failure
                    | Ok view ->
                        return
                            decide request view
                            |> Verdict.outcome
                                (fun proposal -> Previewed.Reviewed(render request view proposal))
                                Previewed.AlreadyAccepted
        }

    let private previewCase (services: ServiceContext) request (ct: CancellationToken) =
        task {
            if ct.IsCancellationRequested then
                return Outcome.Failed CoreFault.StoreUnavailable
            else
                match admitCase request with
                | Error refusal -> return! RequestRefusals.read services ct refusal
                | Ok request ->
                    let! result =
                        services.Store.Read
                            {
                                Work =
                                    fun tx ->
                                        task {
                                            let! view =
                                                ExecutionFacts.case
                                                    services
                                                    tx
                                                    (CaseExecution.operationId request)
                                                    (CaseExecution.target request)

                                            match CaseExecution.complete services request view with
                                            | Error refusal ->
                                                let! refusal =
                                                    ConflictReads.refusal
                                                        services
                                                        tx
                                                        view.Actor
                                                        view.Case
                                                        ChangeEntity.Case
                                                        (CaseExecution.expected request)
                                                        refusal

                                                return
                                                    refusal
                                                    |> Result.map Outcome.Rejected
                                                    |> Result.defaultWith Outcome.Failed
                                            | Ok admitted ->
                                                let! proposed =
                                                    ConflictReads.verdict
                                                        services
                                                        tx
                                                        view.Actor
                                                        view.Case
                                                        ChangeEntity.Case
                                                        (Revision.value admitted.ExpectedRevision)
                                                        (CaseExecution.review services admitted view)

                                                return
                                                    proposed
                                                    |> Verdict.outcome
                                                        (fun (before, after) ->
                                                            advisory
                                                                services
                                                                view.ObservedAt
                                                                admitted.Act
                                                                before
                                                                after
                                                            |> OperationReview.Case
                                                            |> Previewed.Reviewed)
                                                        Previewed.AlreadyAccepted
                                        }
                                CancellationToken = ct
                            }

                    return QueryResults.readResult result
        }

    let private previewAuthority (services: ServiceContext) request ct =
        review
            services
            ct
            (fun () -> admitAuthority request)
            (fun (admitted: AuthorityCommand) ->
                services.Store.Read
                    {
                        Work =
                            fun tx ->
                                ExecutionFacts.target
                                    services
                                    tx
                                    (Authority.factsNeeded admitted.Act)
                                    admitted.OperationId
                                    admitted.Target
                                    (AuthorityRequests.targetCases admitted)
                        CancellationToken = ct
                    })
            (AuthorityExecution.review services)
            (fun _ (view: AuthorityExecutionFacts) proposal ->
                AuthorityReviews.describe services view proposal |> OperationReview.Authority)

    let preview (services: ServiceContext) input ct =
        task {
            let! outcome =
                match input with
                | OperationInput.Case request -> previewCase services request ct
                | OperationInput.Authority request -> previewAuthority services request ct
                | OperationInput.Work request ->
                    match WorkExecution.prepareWork request with
                    | Error refusal -> RequestRefusals.read services ct refusal
                    | Ok prepared -> WorkExecution.preview services prepared ct
                | OperationInput.Note request ->
                    match WorkExecution.prepareNote request with
                    | Error refusal -> RequestRefusals.read services ct refusal
                    | Ok prepared -> WorkExecution.preview services prepared ct
                | OperationInput.Reminder request ->
                    match WorkExecution.prepareReminder request with
                    | Error refusal -> RequestRefusals.read services ct refusal
                    | Ok prepared -> WorkExecution.preview services prepared ct

            return
                {
                    PreviewReply.OperationId = OperationInput.operationId input
                    Outcome = outcome
                }
        }

    let execute (services: ServiceContext) input ct =
        task {
            let! outcome =
                match input with
                | OperationInput.Case request ->
                    write services ct (fun () -> admitCase request) (fun command ->
                        CaseExecution.run services command ct)
                | OperationInput.Work request ->
                    write services ct (fun () -> WorkExecution.prepareWork request) (fun prepared ->
                        WorkExecution.run services prepared ct)
                | OperationInput.Note request ->
                    write services ct (fun () -> WorkExecution.prepareNote request) (fun prepared ->
                        WorkExecution.run services prepared ct)
                | OperationInput.Reminder request ->
                    write
                        services
                        ct
                        (fun () -> WorkExecution.prepareReminder request)
                        (fun prepared -> WorkExecution.run services prepared ct)
                | OperationInput.Authority request ->
                    write services ct (fun () -> admitAuthority request) (fun command ->
                        AuthorityExecution.run services command ct)

            return
                {
                    ExecuteReply.OperationId = OperationInput.operationId input
                    Outcome = outcome
                }
        }

    let matchesInstallation (services: ServiceContext) installation =
        services.InstallationId = installation

    let context services () ct = Queries.context services ct

    let get services (input: CaseGetRequest) ct = Queries.get services input.Target ct

    let notes = NoteQueries.page
    let reminder = WorkQueries.reminder
    let reminders = WorkQueries.reminders
    let workViews = ViewQueries.work

    let assignees = Queries.assignees
    let list = Queries.list
    let caseSummary = Queries.caseSummary
    let timeline = TimelineQueries.read

    let operationGet services (input: OperationLookup) ct =
        task {
            let! outcome = OperationQueries.get services input ct

            return
                {
                    OperationReply.OperationId = input.OperationId
                    Outcome = outcome
                }
        }

    let actors = Queries.actors
    let grants = Queries.grants
    let authorityHistory = Queries.authorityHistory

    let provisionOwnerInTransaction services tx operationId profile roles =
        AuthorityExecution.provisionInTransaction services tx operationId profile roles

    let provisionOwner services operationId profile roles token =
        AuthorityExecution.provisionStore services operationId profile roles token

    let accessRequests = AccessRequestQueries.page
    let dismissAccessRequest = AccessRequestQueries.dismiss
