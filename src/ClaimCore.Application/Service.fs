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

    let private admitCase = CaseOperations.prepare

    let private admitPeople = People.prepare

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

    let private previewCase services request ct =
        match admitCase request with
        | Error refusal -> RequestRefusals.read services ct refusal
        | Ok prepared ->
            SubjectExecution.preview services (CaseOperations.registration services) prepared ct

    let private previewPeople services request ct =
        match admitPeople request with
        | Error refusal -> RequestRefusals.read services ct refusal
        | Ok command ->
            SubjectExecution.preview services (PeopleOperations.registration services) command ct

    let preview (services: ServiceContext) input ct =
        task {
            let! outcome =
                match input with
                | OperationInput.Case request -> previewCase services request ct
                | OperationInput.People request -> previewPeople services request ct
                | OperationInput.Work request ->
                    match WorkOperations.prepareWork request with
                    | Error refusal -> RequestRefusals.read services ct refusal
                    | Ok prepared ->
                        SubjectExecution.preview
                            services
                            (WorkOperations.registration services)
                            prepared
                            ct
                | OperationInput.Note request ->
                    match WorkOperations.prepareNote request with
                    | Error refusal -> RequestRefusals.read services ct refusal
                    | Ok prepared ->
                        SubjectExecution.preview
                            services
                            (WorkOperations.registration services)
                            prepared
                            ct
                | OperationInput.Reminder request ->
                    match WorkOperations.prepareReminder request with
                    | Error refusal -> RequestRefusals.read services ct refusal
                    | Ok prepared ->
                        SubjectExecution.preview
                            services
                            (WorkOperations.registration services)
                            prepared
                            ct

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
                        SubjectExecution.run
                            services
                            (CaseOperations.registration services)
                            command
                            ct)
                | OperationInput.Work request ->
                    write
                        services
                        ct
                        (fun () -> WorkOperations.prepareWork request)
                        (fun prepared ->
                            SubjectExecution.run
                                services
                                (WorkOperations.registration services)
                                prepared
                                ct)
                | OperationInput.Note request ->
                    write
                        services
                        ct
                        (fun () -> WorkOperations.prepareNote request)
                        (fun prepared ->
                            SubjectExecution.run
                                services
                                (WorkOperations.registration services)
                                prepared
                                ct)
                | OperationInput.Reminder request ->
                    write
                        services
                        ct
                        (fun () -> WorkOperations.prepareReminder request)
                        (fun prepared ->
                            SubjectExecution.run
                                services
                                (WorkOperations.registration services)
                                prepared
                                ct)
                | OperationInput.People request ->
                    write services ct (fun () -> admitPeople request) (fun command ->
                        SubjectExecution.run
                            services
                            (PeopleOperations.registration services)
                            command
                            ct)

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
    let payments = PaymentQueries.page

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
    let peopleHistory = Queries.peopleHistory

    let provisionOwnerInTransaction services tx operationId profile roles =
        PeopleOperations.provisionInTransaction services tx operationId profile roles

    let provisionOwner services operationId profile roles token =
        PeopleOperations.provisionStore services operationId profile roles token

    let accessRequests = AccessRequestQueries.page
    let dismissAccessRequest = AccessRequestQueries.dismiss
