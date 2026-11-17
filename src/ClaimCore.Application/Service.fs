namespace ClaimCore.Application

open ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// The typed application boundary owns orchestration; hosts supply immutable request dependencies.
module Service =
    /// Hosts add their instruments to the single application meter.
    let metrics = ApplicationMetrics.meter

    let internal create productVersion installationId store calendar principal =
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

    /// Request-scoped storage decorators stay inside the application.
    let internal mapStore transform (services: ServiceContext) =
        { services with
            Store = transform services.Store
        }

    /// A lookup may require a head-locked absence confirmation; hosting owns its admission lease.
    let withWriteAdmission (acquire: CancellationToken -> Task<IDisposable>) services =
        mapStore
            (fun store ->
                { new IStore with
                    member _.Context query = store.Context query
                    member _.Read operation = store.Read operation

                    member _.Write operation =
                        task {
                            use! _lease = acquire operation.CancellationToken
                            return! store.Write operation
                        }
                })
            services

    let withClaims claims (services: ServiceContext) = { services with Claims = claims }

    let executeSubmission = OperationService.executeSubmission
    let previewSubmission = OperationService.previewSubmission

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

    let internal provisionOwnerInTransaction =
        OperationService.provisionOwnerInTransaction

    let internal provisionOwner = OperationService.provisionOwner

    let accessRequests = AccessRequestQueries.page
    let dismissAccessRequest = AccessRequestQueries.dismiss
