namespace ClaimCore.Application

open System
open System.Threading.Tasks
open ClaimCore.Application.Reads
open System.Globalization
open System.Security.Cryptography
open System.Text
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat

/// Evidence-backed values have no constructor outside this file.
type Released<'value> =
    private
    | Released of 'value

    member this.Value =
        let (Released value) = this
        value

module internal DisclosureReads =
    let private event actorId kind outcome caseId attachmentId =
        {
            DisclosureEvent.ActorId = actorId
            Kind = kind
            CaseId = caseId
            AttachmentId = attachmentId
            Outcome = outcome
            Export = None
            Page = None
        }

    let private actorId principal (view: AccessView) =
        view.Actor
        |> Option.filter (fun actor -> actor.Principal = principal)
        |> Option.map _.ActorId

    let success actorId kind caseId attachmentId =
        event actorId kind DisclosureOutcome.Succeeded caseId attachmentId

    let redacted actor kind =
        event actor kind DisclosureOutcome.Refused None None

    let refused principal view kind =
        actorId principal view |> Option.map (fun id -> redacted id kind)

    let withPage inspect (outcome, event) =
        match outcome with
        | Outcome.Succeeded value ->
            outcome,
            Option.map
                (fun (event: DisclosureEvent) ->
                    { event with
                        Page = Some(inspect value)
                    })
                event
        | _ -> outcome, event

    let observe principal view kind caseId attachmentId outcome =
        match outcome with
        | Outcome.Succeeded _ ->
            match actorId principal view with
            | Some id -> outcome, Some(success id kind caseId attachmentId)
            | None -> Outcome.Failed CoreFault.StoreIntegrityError, None
        | Outcome.Rejected(Refusal.Access _ | Refusal.AttachmentPreviewUnavailable) ->
            outcome, refused principal view kind
        | _ -> outcome, None

    let target selector resolved =
        match selector with
        | CaseSelector.Id id -> Some id
        | CaseSelector.Reference _ -> resolved

    let receiptTarget (change: PartChange) =
        match change.Aggregate with
        | AggregateReference.Person _ -> None
        | AggregateReference.Family caseId ->
            let attachment =
                if change.Kind = PartKind.Attachment then
                    AttachmentId.create change.PartId
                else
                    None

            Some(caseId, attachment)

    let capturedCaseRead principal view kind caseId attachment result =
        match result, actorId principal view with
        | Ok _, Some id -> result, Some(success id kind (Some caseId) attachment)
        | Ok _, None -> Error CoreFault.StoreIntegrityError, None
        | Error _, _ -> result, None

    let private record (store: IStore) token disclosure =
        task {
            match! store.RecordDisclosure(disclosure, token) with
            | Ok() -> return Ok()
            | Error(StoreFailure.Cancelled | StoreFailure.Unavailable | StoreFailure.CommitOutcomeUnknown) ->
                return Error CoreFault.StoreUnavailable
            | Error failure -> return Error(QueryResults.fault failure)
        }

    let private needsEvidence =
        function
        | Refusal.AttachmentDuplicate _ -> true
        | Refusal.RevisionConflict conflict -> conflict.Changes.IsSome || conflict.ReadCaseId.IsSome
        | Refusal.InvalidInput _
        | Refusal.Case _
        | Refusal.Reminder _
        | Refusal.People _
        | Refusal.Access _
        | Refusal.OperationIdOutOfWindow _
        | Refusal.OperationClockUnavailable _
        | Refusal.IdempotencyConflict
        | Refusal.AttachmentCapacityReached
        | Refusal.AttachmentPreviewUnavailable
        | Refusal.InvalidCursor
        | Refusal.TraversalChanged
        | Refusal.CaseDateRange _
        | Refusal.RegisterMaintenance -> false

    let safeOutcome =
        function
        | Outcome.Rejected refusal when not (needsEvidence refusal) ->
            Released(Outcome.Rejected refusal)
        | Outcome.Failed fault -> Released(Outcome.Failed fault)
        | _ -> Released(Outcome.Failed CoreFault.StoreIntegrityError)

    let release (services: ServiceContext) token (outcome, evidence: DisclosureEvent option) =
        task {
            match outcome, evidence with
            | Outcome.Succeeded _, Some event when event.Outcome <> DisclosureOutcome.Succeeded ->
                return Released(Outcome.Failed CoreFault.StoreIntegrityError)
            | Outcome.Rejected refusal, Some event when
                needsEvidence refusal && event.Outcome <> DisclosureOutcome.Succeeded
                ->
                return safeOutcome outcome
            | _, Some event ->
                match! record services.Store token event with
                | Ok() -> return Released outcome
                | Error fault -> return Released(Outcome.Failed fault)
            | _, None -> return safeOutcome outcome
        }

    let complete services token =
        function
        | Ok captured -> release services token captured
        | Error failure -> Task.FromResult(safeOutcome (QueryResults.failure failure))

    /// Preparation can hold a private candidate, but its type cannot reach a transport.
    let completePreparation services token =
        function
        | Ok(outcome, None) -> Task.FromResult outcome
        | Ok(outcome, Some evidence) ->
            task {
                let! result = release services token (outcome, Some evidence)
                return result.Value
            }
        | Error failure -> Task.FromResult(QueryResults.failure failure)

    let content (Released outcome) =
        Outcome.fold (Released >> Outcome.Succeeded) Outcome.Rejected Outcome.Failed outcome

    let lookup id (Released outcome) =
        Released
            {
                OperationReply.OperationId = id
                Outcome = outcome
            }

    let preview id (Released outcome) =
        Released
            {
                PreviewReply.OperationId = id
                Outcome = outcome
            }

    let execute id (Released outcome) =
        Released
            {
                ExecuteReply.OperationId = id
                Outcome = outcome
            }

    let safeExecute =
        function
        | ExecuteResult.Failed fault -> Released(ExecuteResult.Failed fault)
        | ExecuteResult.Rejected refusal when not (needsEvidence refusal) ->
            Released(ExecuteResult.Rejected refusal)
        | ExecuteResult.ReceiptUnavailable -> Released ExecuteResult.ReceiptUnavailable
        | ExecuteResult.OutcomeUnknown -> Released ExecuteResult.OutcomeUnknown
        | _ -> Released(ExecuteResult.Failed CoreFault.StoreIntegrityError)

    let executeReceipt replayed (Released outcome) =
        match outcome with
        | Outcome.Succeeded receipt when replayed -> Released(ExecuteResult.AlreadyAccepted receipt)
        | Outcome.Succeeded receipt -> Released(ExecuteResult.Accepted receipt)
        | Outcome.Rejected _
        | Outcome.Failed _ -> Released ExecuteResult.ReceiptUnavailable

    let executeRefusal (Released outcome: Released<Outcome<unit>>) =
        match outcome with
        | Outcome.Rejected refusal -> Released(ExecuteResult.Rejected refusal)
        | Outcome.Failed fault -> Released(ExecuteResult.Failed fault)
        | Outcome.Succeeded _ -> Released(ExecuteResult.Failed CoreFault.StoreIntegrityError)

    let safeContext (outcome: Outcome<ContextReply>) =
        match outcome with
        | Outcome.Succeeded reply ->
            match reply.Requester with
            | RequesterState.NotRegistered _
            | RequesterState.AccessPending _
            | RequesterState.FormerIdentity
            | RequesterState.SignInRequired -> Released outcome
            | RequesterState.Active _
            | RequesterState.NoRoles _
            | RequesterState.Disabled _ -> safeOutcome outcome
        | _ -> safeOutcome outcome

    let safeTermination =
        function
        | SessionTerminationResult.Failed fault -> Released(SessionTerminationResult.Failed fault)
        | SessionTerminationResult.Rejected refusal when not (needsEvidence refusal) ->
            Released(SessionTerminationResult.Rejected refusal)
        | SessionTerminationResult.RetirementConfirmed ->
            Released SessionTerminationResult.RetirementConfirmed
        | SessionTerminationResult.OutcomeUnknown ->
            Released SessionTerminationResult.OutcomeUnknown
        | _ -> Released(SessionTerminationResult.Failed CoreFault.StoreIntegrityError)

    let bootstrapTermination (value: SessionTermination) =
        if value.Self then
            Released(SessionTerminationResult.Succeeded value)
        else
            Released(SessionTerminationResult.Failed CoreFault.StoreIntegrityError)

    let termination (Released outcome) =
        match outcome with
        | Outcome.Succeeded value -> Released(SessionTerminationResult.Succeeded value)
        | _ -> Released SessionTerminationResult.OutcomeUnknown

    let digest store actor token candidate =
        task {
            match! record store token (success actor DisclosureKind.Digest None None) with
            | Ok() -> return Some(Released candidate)
            | Error _ -> return None
        }

    let exportFailure store actor token =
        record store token (redacted actor DisclosureKind.Export)

    let private readWithEvidence
        finalize
        (services: ServiceContext)
        kind
        (identify: 'value -> CaseId option)
        evidence
        (operation: ReadOperation<Outcome<'value>>)
        =
        task {
            let! captured =
                services.Store.Read
                    {
                        CancellationToken = operation.CancellationToken
                        Work =
                            fun tx ->
                                task {
                                    let! outcome = operation.Work tx

                                    if tx.ExportSnapshot then
                                        return outcome, None
                                    else
                                        let! view = AccessReads.access tx services.Principal None

                                        let subject =
                                            match outcome with
                                            | Outcome.Succeeded value -> identify value
                                            | _ -> None

                                        return
                                            observe
                                                services.Principal
                                                view
                                                kind
                                                subject
                                                None
                                                outcome
                                            |> evidence
                                }
                    }

            return! finalize services operation.CancellationToken captured
        }

    let read services kind identify operation =
        readWithEvidence complete services kind identify id operation

    let pageReadWith finalize services kind identify inspect operation =
        readWithEvidence finalize services kind identify (withPage inspect) operation

    let pageRead services kind identify inspect operation =
        pageReadWith complete services kind identify inspect operation

    let rows items =
        DisclosurePageEvidence.create (List.length items) [] []

    let cases select items =
        DisclosurePageEvidence.create (List.length items) (List.map select items) []

    let people select items =
        DisclosurePageEvidence.create (List.length items) [] (List.map select items)

    let private binding caller (request: DisclosureTrailRequest) =
        let optional render =
            Option.map render >> Option.defaultValue ""

        let instant (value: DateTimeOffset) =
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)

        [
            ActorId.format caller
            optional ActorId.format request.ActorId
            optional CaseId.format request.CaseId
            optional (Vocabulary.token DisclosureKind.vocabulary) request.Kind
            optional instant request.From
            optional instant request.Until
        ]
        |> String.concat "\u001f"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower

    let page (services: ServiceContext) (request: DisclosureTrailRequest) token =
        task {
            let! result =
                services.Store.Read
                    {
                        CancellationToken = token
                        Work =
                            fun tx ->
                                task {
                                    let! view = AccessReads.access tx services.Principal None

                                    match view.Actor with
                                    | Some actor when
                                        Access.can
                                            services.Principal
                                            view
                                            Capability.ReadDisclosureTrail
                                            Scope.Installation
                                        ->
                                        if not (QueryResults.validLimit request.Limit) then
                                            return QueryResults.invalidLimit (), None
                                        else
                                            let scope = binding actor.ActorId request

                                            match
                                                QueryResults.cursor
                                                    (Cursors.decodeDisclosureTrail
                                                        services.InstallationId
                                                        scope)
                                                    request.AfterCursor
                                            with
                                            | Error refusal -> return Outcome.Rejected refusal, None
                                            | Ok continuation ->
                                                let! throughId =
                                                    match continuation with
                                                    | Some(_, throughId) ->
                                                        System.Threading.Tasks.Task.FromResult
                                                            throughId
                                                    | None -> DisclosureStorage.upperBound tx

                                                let! rows =
                                                    DisclosureStorage.page
                                                        tx
                                                        request
                                                        (continuation |> Option.map fst)
                                                        throughId
                                                        (request.Limit + 1)

                                                let page =
                                                    rows
                                                    |> TransactionPages.page
                                                        request.Limit
                                                        (fun row -> row.RecordedAt, row.Id)

                                                let content =
                                                    {
                                                        DisclosureTrailPage.Basis =
                                                            ServiceContext.basis
                                                                services
                                                                tx.ObservedAt
                                                        Items = page.Items
                                                        NextCursor =
                                                            page.Next
                                                            |> Option.map (
                                                                Cursors.encodeDisclosureTrail
                                                                    services.InstallationId
                                                                    scope
                                                                    throughId
                                                            )
                                                    }

                                                let disclosure =
                                                    { success
                                                          actor.ActorId
                                                          DisclosureKind.TrailRead
                                                          None
                                                          None with
                                                        Page =
                                                            Some(
                                                                DisclosurePageEvidence.create
                                                                    content.Items.Length
                                                                    []
                                                                    []
                                                            )
                                                    }

                                                return Outcome.Succeeded content, Some disclosure
                                    | _ ->
                                        return
                                            QueryResults.unavailable,
                                            refused services.Principal view DisclosureKind.TrailRead
                                }
                    }

            return! complete services token result
        }
