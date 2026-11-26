namespace ClaimCore.Application.Reads

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat

module internal DisclosureReads =
    let private event actorId kind outcome caseId attachmentId =
        {
            DisclosureEvent.ActorId = actorId
            Kind = kind
            CaseId = caseId
            AttachmentId = attachmentId
            Outcome = outcome
            Export = None
        }

    let private actorId principal (view: AccessView) =
        view.Actor
        |> Option.filter (fun actor -> actor.Principal = principal)
        |> Option.map _.ActorId

    let success actorId kind caseId attachmentId =
        event actorId kind DisclosureOutcome.Succeeded caseId attachmentId

    let refused principal view caseId attachmentId =
        actorId principal view
        |> Option.map (fun id ->
            event id DisclosureKind.AccessRefused DisclosureOutcome.Refused caseId attachmentId)

    let observe principal view kind caseId attachmentId outcome =
        match outcome with
        | Outcome.Succeeded _ ->
            match actorId principal view with
            | Some id -> outcome, Some(success id kind caseId attachmentId)
            | None -> Outcome.Failed CoreFault.StoreIntegrityError, None
        | Outcome.Rejected(Refusal.Access _ | Refusal.AttachmentPreviewUnavailable) ->
            outcome, refused principal view caseId attachmentId
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

    let capturedCaseRead principal view caseId attachment result =
        match result, actorId principal view with
        | Ok _, Some id -> result, Some(success id DisclosureKind.CaseRead (Some caseId) attachment)
        | Ok _, None -> Error CoreFault.StoreIntegrityError, None
        | Error _, _ -> result, None

    let record (services: ServiceContext) token disclosure =
        task {
            match! services.Store.RecordDisclosure(disclosure, token) with
            | Ok() -> return Ok()
            | Error(StoreFailure.Cancelled | StoreFailure.Unavailable | StoreFailure.CommitOutcomeUnknown) ->
                return Error CoreFault.StoreUnavailable
            | Error failure -> return Error(QueryResults.fault failure)
        }

    let release services token =
        function
        | outcome, None -> System.Threading.Tasks.Task.FromResult outcome
        | outcome, Some disclosure ->
            task {
                match! record services token disclosure with
                | Ok() -> return outcome
                | Error fault -> return Outcome.Failed fault
            }

    let complete services token =
        function
        | Ok captured -> release services token captured
        | Error failure -> System.Threading.Tasks.Task.FromResult(QueryResults.failure failure)

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
                                            | Ok before ->
                                                let! rows =
                                                    DisclosureStorage.page
                                                        tx
                                                        request
                                                        before
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
                                                            )
                                                    }

                                                return
                                                    Outcome.Succeeded content,
                                                    Some(
                                                        success
                                                            actor.ActorId
                                                            DisclosureKind.TrailRead
                                                            None
                                                            None
                                                    )
                                    | _ -> return QueryResults.unavailable, None
                                }
                    }

            return! complete services token result
        }
