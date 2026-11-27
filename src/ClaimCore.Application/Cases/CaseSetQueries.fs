namespace ClaimCore.Application.Reads

open ClaimCore.Application.Storage

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

/// Authorizes the read and resolves its result within one store snapshot.
module internal CaseSetQueries =
    open QueryResults

    let selectCases
        principal
        basis
        (request: CaseListRequest)
        actor
        : Result<TransactionPages.CaseQuery, Refusal> =
        currentActor principal actor
        |> Result.bind (fun actor ->
            match Access.visibility principal actor with
            | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            | Some visibility ->
                CaseListFilter.admit request.Filter
                |> Result.mapError (function
                    | CaseSelectionIssue.Text violation ->
                        Refusal.invalidInput (InputTarget.Text, violation)
                    | CaseSelectionIssue.ReversedRange upper -> Refusal.CaseDateRange upper)
                |> Result.map (fun filter ->
                    {
                        TransactionPages.CaseQuery.Selection =
                            {
                                DateBasis = basis
                                Visibility = visibility
                                ActorId = actor.Actor |> Option.map _.ActorId |> Option.get
                                ReadRoles = Access.readRoles principal
                                ForbiddenRoles = Access.invalidRoles principal
                                Filter = filter
                            }
                        Limit = request.Page.Limit
                        AfterPosition = None
                    }))

    let private scopedCasePage (tx: ReadTx) (query: TransactionPages.CaseQuery) after =
        let selected = query.Selection

        ScopedTraversal.page
            tx
            "case-traversal"
            selected.ActorId
            selected.DateBasis.BusinessDate
            (Cursors.decodeCases tx.InstallationId selected.Filter)
            (Cursors.encodeCases tx.InstallationId selected.Filter)
            (fun () ->
                TransactionPages.caseTraversalWitness tx.Connection selected tx.CancellationToken)
            (fun position -> FamilyReads.casePage tx { query with AfterPosition = position })
            _.Next
            after

    let private summary
        installation
        principal
        actor
        paused
        (work: CaseWorkView)
        next
        lastPayment
        parties
        record
        =
        CaseRecords.current installation parties record
        |> Result.map (fun _ ->
            {
                CaseSummary.CaseId = record.Snapshot.CaseId
                Reference = record.Snapshot.Reference
                Revision = record.Snapshot.Revision
                WorkRevision = work.Revision
                CanAssign = CaseWorkReads.canAssign principal actor paused record.Snapshot
                ChangedAt = record.Index.ChangedAt
                Headline = CaseHeadline.ofState (CaseWorkReads.facts record work).State
                Stages = CaseStages.outline record.Snapshot.State
                Attention =
                    CaseAttention.at work.Basis.BusinessDate (CaseWorkReads.facts record work)
                NextAction =
                    next
                    |> Option.map (fun reminder ->
                        {
                            CaseNextAction.ReminderId = Reminder.id reminder
                            Action = Reminder.action reminder
                            Due = Reminder.due reminder
                            DueState = work.Due
                        })
                Responsible = work.Responsible
                PendingReminders = work.PendingReminders
                NetPaid =
                    CaseState.fields record.Snapshot.State
                    |> Option.bind (fun fields -> FinancialPosition.totals fields.Finance)
                PaymentCount =
                    CaseState.fields record.Snapshot.State
                    |> Option.map (fun fields -> fields.Finance.PaymentCount)
                    |> Option.defaultValue 0L
                LastPaymentDate = lastPayment
                State =
                    CaseState.map
                        (fun (fields: CaseFields) ->
                            {
                                CaseSummaryFacts.Fields =
                                    CaseSummaryFields.values CaseSummaryFields.declarations fields
                                ClosureOutcome =
                                    Handling.closure fields.Handling |> Option.map _.Outcome
                                Parties =
                                    Vocabulary.all PartyRole.vocabulary
                                    |> List.map (fun role ->
                                        role, PartyContent.names role parties)
                                    |> Map.ofList
                            })
                        record.Snapshot.State
            })

    let private summaries
        (services: ServiceContext)
        (tx: ReadTx)
        basis
        paused
        (items: CasePageItem list)
        =
        task {
            let records = items |> List.map _.Case

            let supplied =
                items
                |> List.choose (fun item ->
                    item.WorkSummary |> Option.map (fun work -> item.Case.CaseId, work))
                |> Map.ofList

            let! work = CaseWorkReads.viewsFromSummaries tx basis records supplied

            let! parties =
                PartyStorage.current
                    tx.Connection
                    (records |> List.map _.CaseId)
                    tx.CancellationToken

            let! actor =
                AccessReads.accessCases tx (services.Principal, records |> List.map _.CaseId)

            let! paidDates =
                PaymentStorage.latestPaidDates
                    tx.Connection
                    (records |> List.map _.CaseId)
                    tx.CancellationToken

            return
                work
                |> Result.bind (fun work ->
                    records
                    |> List.fold
                        (fun accumulated record ->
                            accumulated
                            |> Result.bind (fun values ->
                                summary
                                    services.InstallationId
                                    services.Principal
                                    actor
                                    paused
                                    (Map.find record.CaseId work)
                                    (supplied |> Map.tryFind record.CaseId |> Option.bind _.Next)
                                    (Map.tryFind record.CaseId paidDates)
                                    (Map.tryFind record.CaseId parties |> Option.defaultValue [])
                                    record
                                |> Result.map (fun value -> value :: values)))
                        (Ok [])
                    |> Result.map List.rev)
        }

    let private exactReference (services: ServiceContext) (tx: ReadTx) text =
        task {
            match text |> Option.bind (CaseReference.parse >> Result.toOption) with
            | None -> return None
            | Some reference ->
                let text = CaseReference.format reference
                let! record = FamilyReads.findCaseByReference tx text

                match record with
                | None -> return None
                | Some record ->
                    let! actor = AccessReads.access tx services.Principal (Some record.CaseId)

                    if
                        CaseRecords.authorize
                            services.Principal
                            actor
                            Capability.ReadCase
                            (Some record)
                    then
                        return
                            Some
                                {
                                    ExactCaseReference.CaseId = record.CaseId
                                    CaseReference = record.Snapshot.Reference
                                }
                    else
                        return None
        }

    let private listWith
        finalize
        reject
        (services: ServiceContext)
        (request: CaseListRequest)
        token
        =
        let principal = services.Principal

        task {
            if not (validLimit request.Page.Limit) then
                return! reject services token QueryResults.invalidLimitRefusal
            else
                let! result =
                    DisclosureReads.pageReadWith
                        finalize
                        services
                        DisclosureKind.CaseList
                        (fun _ -> None)
                        (fun (page: CaseSummaryPage) ->
                            let cases = page.Items |> List.map _.CaseId
                            let extra = page.ExactReference |> Option.map _.CaseId |> Option.toList
                            DisclosurePageEvidence.create page.Items.Length (cases @ extra) [])
                        ({
                            ReadOperation.Work =
                                (fun tx ->
                                    task {
                                        let! facts = AccessReads.globalReadFacts tx principal
                                        let actor = facts.People

                                        match
                                            selectCases
                                                principal
                                                (ServiceContext.basis services tx.ObservedAt)
                                                request
                                                actor
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok selection ->
                                            match!
                                                scopedCasePage
                                                    tx
                                                    selection
                                                    request.Page.AfterCursor
                                            with
                                            | Error refusal -> return Outcome.Rejected refusal
                                            | Ok(page, next) ->
                                                let maintenance = facts.Maintenance

                                                if page.Items.Length > Paging.MaximumPageSize then
                                                    return
                                                        Outcome.Failed
                                                            CoreFault.StoreIntegrityError
                                                else
                                                    let! items =
                                                        summaries
                                                            services
                                                            tx
                                                            selection.Selection.DateBasis
                                                            facts.Maintenance.Active
                                                            page.Items

                                                    match items with
                                                    | Error fault -> return Outcome.Failed fault
                                                    | Ok items ->
                                                        let! exact =
                                                            exactReference
                                                                services
                                                                tx
                                                                request.Filter.Text

                                                        let! authorizedScopeEmpty =
                                                            if
                                                                List.isEmpty items
                                                                && page.Next.IsNone
                                                                && request.Page.AfterCursor.IsNone
                                                                && exact.IsNone
                                                            then
                                                                task {
                                                                    let scope =
                                                                        { selection.Selection with
                                                                            Filter =
                                                                                CaseListFilter
                                                                                    .empty
                                                                        }

                                                                    let! exists =
                                                                        FamilyReads.caseExists
                                                                            tx
                                                                            scope

                                                                    return not exists
                                                                }
                                                            else
                                                                System.Threading.Tasks
                                                                    .Task.FromResult
                                                                    false

                                                        return
                                                            Outcome.Succeeded
                                                                {
                                                                    CaseSummaryPage.Basis =
                                                                        selection.Selection
                                                                            .DateBasis
                                                                    Items = items
                                                                    ExactReference = exact
                                                                    AuthorizedScopeEmpty =
                                                                        authorizedScopeEmpty
                                                                    Offers =
                                                                        if maintenance.Active then
                                                                            []
                                                                        else
                                                                            Advice
                                                                                .installationOffers
                                                                                principal
                                                                                actor
                                                                    NextCursor = next
                                                                }

                                    })
                            CancellationToken = token
                        })

                return result
        }

    let list services request token =
        listWith DisclosureReads.complete RequestRefusals.protectedRead services request token

    let listForExport services request token =
        listWith DisclosureReads.completePreparation RequestRefusals.read services request token

    let private caseSummaryWith
        finalize
        reject
        (services: ServiceContext)
        (summary: CaseSummaryRequest)
        token
        =
        task {
            if not (validLimit summary.Page.Limit) then
                return! reject services token QueryResults.invalidLimitRefusal
            else
                let! result =
                    DisclosureReads.pageReadWith
                        finalize
                        services
                        DisclosureKind.CaseSummary
                        (fun _ -> None)
                        (fun (page: CaseWorklistSummary) -> DisclosureReads.rows page.Rows)
                        {
                            Work =
                                fun tx ->
                                    task {
                                        let! actor = AccessReads.access tx services.Principal None
                                        let basis = ServiceContext.basis services tx.ObservedAt

                                        let request =
                                            {
                                                CaseListRequest.Page =
                                                    {
                                                        Limit = summary.Page.Limit
                                                        AfterCursor = None
                                                    }
                                                Filter = summary.Selection.Filter
                                            }

                                        match
                                            selectCases services.Principal basis request actor
                                        with
                                        | Error refusal -> return Outcome.Rejected refusal
                                        | Ok selected ->
                                            let selection = selected.Selection
                                            let grouping = summary.Selection.GroupBy
                                            let date = CalendarDate.ofDate basis.BusinessDate

                                            let encode =
                                                Cursors.encodeCaseReport
                                                    services.InstallationId
                                                    selection.ActorId
                                                    date
                                                    selection.Filter
                                                    grouping

                                            let cacheScope =
                                                Cursors.caseReportScope
                                                    services.InstallationId
                                                    selection.ActorId
                                                    date
                                                    selection.Filter
                                                    grouping

                                            let scope =
                                                ScopedTraversal.report
                                                    tx
                                                    "case-report-traversal"
                                                    selection.ActorId
                                                    basis.BusinessDate
                                                    (Cursors.decodeCaseReport
                                                        services.InstallationId
                                                        selection.ActorId
                                                        date
                                                        selection.Filter
                                                        grouping)
                                                    encode
                                                    (fun () ->
                                                        CaseSummaryQuery.witness
                                                            tx.Connection
                                                            selection
                                                            grouping
                                                            token)
                                                    (fun witness position ->
                                                        FamilyReads.caseSummary
                                                            tx
                                                            selection
                                                            grouping
                                                            position
                                                            summary.Page.Limit
                                                            (witness
                                                             |> Option.map (fun digest ->
                                                                 cacheScope, digest)))
                                                    (fun (_, _, next) -> next)
                                                    summary.Page.AfterCursor

                                            match! scope with
                                            | Error refusal -> return Outcome.Rejected refusal
                                            | Ok((counts, rows, _), next) ->
                                                return
                                                    Outcome.Succeeded
                                                        {
                                                            Basis = basis
                                                            Counts = counts
                                                            GroupBy = grouping
                                                            Rows = rows
                                                            NextCursor = next
                                                        }
                                    }
                            CancellationToken = token
                        }

                return result
        }

    let caseSummary services request token =
        caseSummaryWith
            DisclosureReads.complete
            RequestRefusals.protectedRead
            services
            request
            token

    let caseSummaryForExport services request token =
        caseSummaryWith
            DisclosureReads.completePreparation
            RequestRefusals.read
            services
            request
            token
