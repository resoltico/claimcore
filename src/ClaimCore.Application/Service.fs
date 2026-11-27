namespace ClaimCore.Application

open ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Private snapshot membership and requester evidence carried between preparation and release.
type ExportReadBasis =
    internal
        {
            Installation: Guid
            Actor: ActorId
            GroupMembers: string option
        }

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
                    member _.RecordDisclosure(event, token) = store.RecordDisclosure(event, token)

                    member _.Write operation =
                        task {
                            use! _lease = acquire operation.CancellationToken
                            return! store.Write operation
                        }
                })
            services

    /// Each read and disclosure phase releases its capacity after the provider completes cleanup.
    let withReadAdmission (acquire: CancellationToken -> Task<IDisposable>) services =
        let admitted token run =
            task {
                use! _lease = acquire token
                return! run ()
            }

        mapStore
            (fun store ->
                { new IStore with
                    member _.Context query =
                        admitted query.CancellationToken (fun () -> store.Context query)

                    member _.Read operation =
                        admitted operation.CancellationToken (fun () -> store.Read operation)

                    member _.RecordDisclosure(event, token) =
                        admitted token (fun () -> store.RecordDisclosure(event, token))

                    member _.Write operation = store.Write operation
                })
            services

    /// A caller can observe an interrupted read before the query maps store failures to replies.
    let withReadCancellation (cancellation: CancellationToken) services =
        let propagate (token: CancellationToken) pending =
            task {
                match! pending with
                | Error StoreFailure.Cancelled when
                    cancellation.IsCancellationRequested && token.IsCancellationRequested
                    ->
                    return! Task.FromCanceled<_> cancellation
                | result -> return result
            }

        mapStore
            (fun store ->
                { new IStore with
                    member _.Context query = store.Context query

                    member _.Read operation =
                        store.Read operation |> propagate operation.CancellationToken

                    member _.RecordDisclosure(event, token) =
                        store.RecordDisclosure(event, token) |> propagate token

                    member _.Write operation = store.Write operation
                })
            services

    /// Run the entire bounded export traversal through one read-only provider snapshot.
    let readExportSnapshot (services: ServiceContext) selection token work =
        task {
            let! result =
                services.Store.Read
                    {
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    let! actor = AccessReads.access tx services.Principal None

                                    match actor.Actor with
                                    | Some requester when Access.validActor services.Principal actor ->
                                        let! membership =
                                            task {
                                                match selection with
                                                | ExportSelection.CaseSummary summary ->
                                                    let request =
                                                        {
                                                            CaseListRequest.Filter = summary.Filter
                                                            Page =
                                                                {
                                                                    Limit = Paging.MaximumPageSize
                                                                    AfterCursor = None
                                                                }
                                                        }

                                                    match
                                                        CaseSetQueries.selectCases
                                                            services.Principal
                                                            (ServiceContext.basis
                                                                services
                                                                tx.ObservedAt)
                                                            request
                                                            actor
                                                    with
                                                    | Error refusal -> return Error refusal
                                                    | Ok query ->
                                                        let! witness =
                                                            CaseSummaryQuery.witness
                                                                tx.Connection
                                                                query.Selection
                                                                summary.GroupBy
                                                                token

                                                        return Ok(Some witness)
                                                | _ -> return Ok None
                                            }

                                        match membership with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok members ->
                                            let exportTx = tx.ForExport

                                            let snapshot =
                                                mapStore
                                                    (fun _ ->
                                                        { new IStore with
                                                            member _.Read operation =
                                                                task {
                                                                    operation.CancellationToken
                                                                        .ThrowIfCancellationRequested()

                                                                    let! value =
                                                                        operation.Work exportTx

                                                                    return Ok value
                                                                }

                                                            member _.Context _ =
                                                                invalidOp
                                                                    "Export cannot change requester context."

                                                            member _.Write _ =
                                                                invalidOp
                                                                    "Export cannot change register state."

                                                            member _.RecordDisclosure(_, _) =
                                                                invalidOp
                                                                    "Export disclosure must follow the release read."
                                                        })
                                                    services

                                            let! rendered = work snapshot

                                            return
                                                Outcome.Succeeded(
                                                    rendered,
                                                    {
                                                        Installation = tx.InstallationId
                                                        Actor = requester.ActorId
                                                        GroupMembers = members
                                                    }
                                                )
                                    | _ -> return QueryResults.unavailable
                                }
                    }

            return QueryResults.readResult result
        }

    let releaseExport
        (services: ServiceContext)
        (before, manifest)
        (after, current)
        caseId
        export
        token
        =
        if before <> after || manifest <> current then
            Task.FromResult(QueryResults.unavailable)
        else
            let disclosure =
                { DisclosureReads.success after.Actor DisclosureKind.Export caseId None with
                    Export = Some export
                }

            DisclosureReads.release services token (Outcome.Succeeded(), Some disclosure)

    let withClaims claims (services: ServiceContext) = { services with Claims = claims }

    let executeSubmission = OperationService.executeSubmission
    let previewSubmission = OperationService.previewSubmission
    let attachmentUploadIntent = OperationService.attachmentUploadIntent
    let executeAttachmentUpload = OperationService.executeAttachmentUpload
    let caseAttachments = AttachmentReads.page
    let downloadAttachment = AttachmentReads.content false
    let previewAttachment = AttachmentReads.content true

    let matchesInstallation (services: ServiceContext) installation =
        services.InstallationId = installation

    let context services () ct =
        RequesterContextQueries.context services ct

    let get services (input: CaseGetRequest) ct =
        CaseDetailQueries.get services input.Target ct

    let notes = NoteQueries.page
    let reminder = WorkQueries.reminder
    let reminders = WorkQueries.reminders
    let workViews = ViewQueries.work

    let assignees = CaseLookupQueries.assignees
    let parties = CaseLookupQueries.parties
    let list = CaseSetQueries.list
    let caseSummary = CaseSetQueries.caseSummary
    let payments = PaymentQueries.page

    let timeline = TimelineQueries.read
    let timelineForExport = TimelineQueries.forExport
    let disclosureTrail = DisclosureReads.page

    let operationGet services (input: OperationLookup) ct =
        task {
            let! outcome = OperationQueries.get services input ct

            return
                {
                    OperationReply.OperationId = input.OperationId
                    Outcome = outcome
                }
        }

    let actors = PeopleQueries.actors
    let grants = PeopleQueries.grants
    let peopleHistory = PeopleQueries.history

    let internal provisionOwnerInTransaction =
        OperationService.provisionOwnerInTransaction

    let internal provisionOwner = OperationService.provisionOwner

    let accessRequests = AccessRequestQueries.page
    let dismissAccessRequest = AccessRequestQueries.dismiss

    let terminateOwnSessions = SessionTerminationQueries.self
    let terminatePersonSessions = SessionTerminationQueries.person
