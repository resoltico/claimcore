namespace ClaimCore.Application

open ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Candidate queries available only while preparing or rechecking a server export.
[<NoEquality; NoComparison>]
type ExportSnapshot =
    {
        Cases: CaseListRequest -> CancellationToken -> Task<Outcome<CaseSummaryPage>>
        Summary: CaseSummaryRequest -> CancellationToken -> Task<Outcome<CaseWorklistSummary>>
        Timeline: CaseTimelineRequest -> CancellationToken -> Task<Outcome<CaseTimelinePage>>
        Reminders: ReminderListRequest -> CancellationToken -> Task<Outcome<ReminderPage>>
        PeopleHistory: PeopleHistoryRequest -> CancellationToken -> Task<Outcome<PeopleHistoryPage>>
    }

type ExportBody =
    {
        Bytes: byte array
        NextCursor: string option
    }

/// A transport candidate carries its ordered subject and encoded-content fingerprint.
type ExportCandidate =
    {
        Body: ExportBody
        RowCount: int
        Manifest: string
        CaseId: CaseId option
    }

/// Private snapshot membership and requester evidence carried between preparation and release.
type private ExportReadBasis =
    {
        Installation: Guid
        Actor: ActorId
        Credential: (PrincipalKey * VerifiedCredential) option
        EncryptionGeneration: Guid
        GroupMembers: string option
    }

/// Fingerprint of the canonical export selection and paging options supplied by the transport.
type ExportContinuationRequest =
    {
        SelectionDigest: string
        After: string option
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

    let private exportContinuation
        (tx: ReadTx)
        requester
        (request: ExportContinuationRequest option)
        =
        match request with
        | None -> Ok(None, id)
        | Some request ->
            let commitment cursor =
                [
                    tx.InstallationId.ToString("D")
                    ActorId.format requester
                    request.SelectionDigest
                    cursor
                ]
                |> String.concat "\u001f"
                |> Encoding.UTF8.GetBytes
                |> tx.Cipher.ExportCursorCommitment
                |> Convert.ToHexStringLower

            let seal cursor = cursor + "." + commitment cursor

            match request.After with
            | None -> Ok(None, seal)
            | Some value ->
                let delimiter = value.LastIndexOf '.'

                if delimiter <= 0 || value.Length - delimiter <> 65 then
                    Error Refusal.InvalidCursor
                else
                    let cursor = value.Substring(0, delimiter)
                    let suppliedTag = Encoding.ASCII.GetBytes(value.Substring(delimiter + 1))
                    let expectedTag = Encoding.ASCII.GetBytes(commitment cursor)

                    if CryptographicOperations.FixedTimeEquals(suppliedTag, expectedTag) then
                        Ok(Some cursor, seal)
                    else
                        Error Refusal.InvalidCursor

    let internal exportSource services =
        {
            Cases = CaseSetQueries.listForExport services
            Summary = CaseSetQueries.caseSummaryForExport services
            Timeline = TimelineQueries.forExport services
            Reminders = WorkQueries.remindersForExport services
            PeopleHistory = PeopleQueries.historyForExport services
        }

    /// Run the entire bounded export traversal through one read-only provider snapshot.
    let private readExportSnapshot (services: ServiceContext) selection continuation token work =
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
                                        match
                                            exportContinuation tx requester.ActorId continuation
                                        with
                                        | Error refusal ->
                                            return Outcome.Rejected refusal, Some requester.ActorId
                                        | Ok(after, seal) ->
                                            let! membership =
                                                task {
                                                    match selection with
                                                    | ExportSelection.CaseSummary summary ->
                                                        let request =
                                                            {
                                                                CaseListRequest.Filter =
                                                                    summary.Filter
                                                                Page =
                                                                    {
                                                                        Limit =
                                                                            Paging.MaximumPageSize
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
                                            | Error refusal ->
                                                return
                                                    Outcome.Rejected refusal, Some requester.ActorId
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

                                                let! rendered =
                                                    work (exportSource snapshot) after seal

                                                return
                                                    Outcome.Succeeded(
                                                        rendered,
                                                        {
                                                            Installation = tx.InstallationId
                                                            Actor = requester.ActorId
                                                            Credential = tx.Credential
                                                            EncryptionGeneration =
                                                                tx.Cipher.Generation
                                                            GroupMembers = members
                                                        }
                                                    ),
                                                    Some requester.ActorId
                                    | _ ->
                                        return
                                            QueryResults.unavailable,
                                            actor.Actor |> Option.map _.ActorId
                                }
                    }

            match result with
            | Ok observation -> return observation
            | Error failure -> return QueryResults.failure failure, None
        }

    let private releaseExport
        (services: ServiceContext)
        (before, manifest)
        (after, current)
        caseId
        export
        (body: ExportBody)
        token
        =
        task {
            if before <> after || manifest <> current then
                let refusal = DisclosureReads.redacted after.Actor DisclosureKind.Export

                return!
                    DisclosureReads.release services token (QueryResults.unavailable, Some refusal)
            else
                let disclosure =
                    { DisclosureReads.success after.Actor DisclosureKind.Export caseId None with
                        Export = Some export
                    }

                return!
                    DisclosureReads.release services token (Outcome.Succeeded body, Some disclosure)
        }

    /// Prepare and re-observe within application-owned snapshots, then require durable evidence
    /// before any candidate reaches a transport.
    let export
        services
        selection
        (continuation: ExportContinuationRequest option)
        token
        materialize
        =
        let read () =
            readExportSnapshot services selection continuation token materialize

        let refuse actor outcome =
            let event =
                actor
                |> Option.map (fun id -> DisclosureReads.redacted id DisclosureKind.Export)

            task {
                let! released = DisclosureReads.release services token (outcome, event)

                return
                    Outcome.fold
                        (fun _ -> Outcome.Failed CoreFault.StoreIntegrityError)
                        Outcome.Rejected
                        Outcome.Failed
                        released.Value
            }

        task {
            match! read () with
            | (Outcome.Succeeded(Ok prepared, before), _) ->
                match! read () with
                | (Outcome.Succeeded(Ok current, after), _) ->
                    let disclosure =
                        ExportSelection.disclosure prepared.RowCount continuation.IsSome selection

                    let! released =
                        releaseExport
                            services
                            (before, (prepared.Manifest, prepared.Body.NextCursor.IsSome))
                            (after, (current.Manifest, current.Body.NextCursor.IsSome))
                            prepared.CaseId
                            disclosure
                            { prepared.Body with
                                NextCursor = current.Body.NextCursor
                            }
                            token

                    return
                        Outcome.fold
                            (Ok >> Outcome.Succeeded)
                            Outcome.Rejected
                            Outcome.Failed
                            (DisclosureReads.content released)
                | (Outcome.Succeeded(Error _, _), actor) ->
                    return! refuse actor QueryResults.unavailable
                | (Outcome.Rejected refusal, actor) ->
                    return! refuse actor (Outcome.Rejected refusal)
                | (Outcome.Failed fault, _) -> return Outcome.Failed fault
            | (Outcome.Succeeded(Error failure, _), actor) ->
                match actor with
                | None -> return Outcome.Failed CoreFault.StoreIntegrityError
                | Some id ->
                    match! DisclosureReads.exportFailure services.Store id token with
                    | Ok() -> return Outcome.Succeeded(Error failure)
                    | Error fault -> return Outcome.Failed fault
            | (Outcome.Rejected refusal, actor) -> return! refuse actor (Outcome.Rejected refusal)
            | (Outcome.Failed fault, _) -> return Outcome.Failed fault
        }

    let withClaims claims (services: ServiceContext) = { services with Claims = claims }

    let withCredential proof (services: ServiceContext) =
        let evidence = Some(services.Principal, proof)

        mapStore
            (fun store ->
                { new IStore with
                    member _.Context query =
                        store.Context { query with Credential = evidence }

                    member _.RecordDisclosure(event, token) = store.RecordDisclosure(event, token)

                    member _.Read operation =
                        store.Read
                            { operation with
                                Work =
                                    fun tx ->
                                        tx.Credential <- evidence
                                        operation.Work tx
                            }

                    member _.Write operation =
                        store.Write
                            { operation with
                                Work =
                                    fun tx ->
                                        tx.Credential <- evidence
                                        operation.Work tx
                            }
                })
            services

    let attachmentUploadIntent = OperationService.attachmentUploadIntent

    let matchesInstallation (services: ServiceContext) installation =
        services.InstallationId = installation

    let internal provisionOwnerInTransaction =
        OperationService.provisionOwnerInTransaction

    let internal provisionOwner = OperationService.provisionOwner
    let internal timelineForExport = TimelineQueries.forExport
    let requestAccess = AccessRequestQueries.submit
    let dismissAccessRequest = AccessRequestQueries.dismiss

    /// This transport port only binds inputs. Query producers must supply release evidence.
    module Disclosure =
        let unavailableRead () =
            DisclosureReads.safeOutcome (Outcome.Failed CoreFault.StoreUnavailable)

        let unavailableLookup (input: OperationLookup) =
            DisclosureReads.lookup input.OperationId (unavailableRead ())

        let unavailablePreview (input: OperationSubmission) =
            DisclosureReads.preview input.OperationId (unavailableRead ())

        let unavailableExecute (input: OperationSubmission) =
            DisclosureReads.safeExecute (ExecuteResult.Failed CoreFault.StoreUnavailable)
            |> DisclosureReads.execute input.OperationId

        let unavailableTermination () =
            DisclosureReads.safeTermination (
                SessionTerminationResult.Failed CoreFault.StoreUnavailable
            )

        let context services () token =
            RequesterContextQueries.context services token

        let get services (input: CaseGetRequest) token =
            CaseDetailQueries.get services input.Target token

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
        let disclosureTrail = DisclosureReads.page
        let actors = PeopleQueries.actors
        let grants = PeopleQueries.grants
        let peopleHistory = PeopleQueries.history
        let accessRequests = AccessRequestQueries.page
        let previewSubmission = OperationService.previewSubmission
        let executeSubmission = OperationService.executeSubmission
        let executeAttachmentUpload = OperationService.executeAttachmentUpload
        let terminateOwnSessions = SessionTerminationQueries.self
        let terminatePersonSessions = SessionTerminationQueries.person

        let operationGet services (input: OperationLookup) token =
            task {
                let! outcome = OperationQueries.get services input token
                return DisclosureReads.lookup input.OperationId outcome
            }

        let caseAttachments services (input: CaseGetRequest) token =
            AttachmentReads.page services input.Target token

        let private attachment preview services input token =
            task {
                let! result = AttachmentReads.content preview services input token
                return DisclosureReads.content result
            }

        let downloadAttachment = attachment false
        let previewAttachment = attachment true

    // Application callers can inspect completed replies without the transport wrapper.
    let private inspect (result: Task<Released<'value>>) =
        task {
            let! result = result
            return result.Value
        }

    let private completed operation services input token =
        inspect (operation services input token)

    let internal context = completed Disclosure.context
    let internal get = completed Disclosure.get
    let internal notes = completed Disclosure.notes
    let internal reminder = completed Disclosure.reminder
    let internal reminders = completed Disclosure.reminders
    let internal workViews = completed Disclosure.workViews
    let internal assignees = completed Disclosure.assignees
    let internal parties = completed Disclosure.parties
    let internal list = completed Disclosure.list
    let internal caseSummary = completed Disclosure.caseSummary
    let internal payments = completed Disclosure.payments
    let internal timeline = completed Disclosure.timeline
    let internal disclosureTrail = completed Disclosure.disclosureTrail
    let internal operationGet = completed Disclosure.operationGet
    let internal actors = completed Disclosure.actors
    let internal grants = completed Disclosure.grants
    let internal peopleHistory = completed Disclosure.peopleHistory
    let internal accessRequests = completed Disclosure.accessRequests
    let internal previewSubmission = completed Disclosure.previewSubmission
    let internal executeSubmission = completed Disclosure.executeSubmission
    let internal terminateOwnSessions = completed Disclosure.terminateOwnSessions
    let internal terminatePersonSessions = completed Disclosure.terminatePersonSessions

    let internal executeAttachmentUpload services submission bytes token =
        inspect (Disclosure.executeAttachmentUpload services submission bytes token)

    let internal caseAttachments = completed AttachmentReads.page
    let internal downloadAttachment = completed (AttachmentReads.content false)
    let internal previewAttachment = completed (AttachmentReads.content true)
