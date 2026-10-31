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
            Principal = principal
        }

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

    let private advisory (services: ServiceContext) instant act before after =
        let productVersion = services.ProductVersion
        let calendar = services.Calendar
        let before = before |> Option.map Case.snapshot
        let after = Case.snapshot after

        {
            Before = before
            Proposed = after
            Changes = CaseChanges.review act before after
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
                return Outcome.Failed CoreFault.StoreUnavailable
            else
                match admit () with
                | Error refusal -> return Outcome.Rejected refusal
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
                | Error refusal -> return Outcome.Rejected refusal
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

                                            return
                                                match CaseExecution.complete request view.Case with
                                                | Error refusal -> Outcome.Rejected refusal
                                                | Ok admitted ->
                                                    CaseExecution.review services admitted view
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
                                    (AuthorityRequests.targetCase admitted)
                        CancellationToken = ct
                    })
            (AuthorityExecution.review services)
            (fun _ (view: AuthorityExecutionFacts) (_, projection) ->
                AuthorityReviews.describe view projection |> OperationReview.Authority)

    let preview (services: ServiceContext) input ct =
        task {
            let! outcome =
                match input with
                | OperationInput.Case request -> previewCase services request ct
                | OperationInput.Authority request -> previewAuthority services request ct

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
                    write ct (fun () -> admitCase request) (fun command ->
                        CaseExecution.run services command ct)
                | OperationInput.Authority request ->
                    write ct (fun () -> admitAuthority request) (fun command ->
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

    let list = Queries.list
    let caseSummary = Queries.caseSummary
    let history = Queries.history

    let observe services (input: OperationLookup) ct =
        task {
            let! outcome = Queries.observe services input.OperationId ct

            return
                {
                    OperationReply.OperationId = input.OperationId
                    Outcome = outcome
                }
        }

    let resolve services (input: OperationLookup) ct =
        task {
            let! outcome = Resolution.run services input ct

            return
                {
                    OperationReply.OperationId = input.OperationId
                    Outcome = outcome
                }
        }

    let actors = Queries.actors
    let grants = Queries.grants
    let authorityHistory = Queries.authorityHistory

    let provisionOwner services operationId token =
        AuthorityExecution.provisionStore services operationId token
