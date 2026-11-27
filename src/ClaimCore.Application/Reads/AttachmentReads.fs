namespace ClaimCore.Application.Reads

open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Execution
open ClaimCore.Application.Storage
open ClaimCore.Domain
open ClaimCore.Domain.Changes

module internal AttachmentReads =
    let disclosure (tx: ReadTx) caseId =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT coalesce(max(operation_sequence),0)::bigint FROM claimcore.ledger_effects WHERE aggregate_kind=$1 AND aggregate_id=$2 AND (part_kind=$3 OR erases_kind=$1)"
                    [|
                        box "FAMILY"
                        box (CaseId.value caseId)
                        box (PartKind.token PartKind.Attachment)
                    |]
                    tx.CancellationToken

            return
                DatabaseSession.value<int64> 0 rows.Head
                |> DatabaseSession.admitted Sequence.create
        }

    let private withReadable (services: ServiceContext) finalize reject target token capture read =
        task {
            match CaseRecords.validateSelector target with
            | Error refusal -> return! reject services token refusal
            | Ok() ->
                let! result =
                    services.Store.Read
                        {
                            CancellationToken = token
                            Work =
                                fun tx ->
                                    task {
                                        let! view = CaseReadContext.load services tx target

                                        let! outcome =
                                            match view.Case with
                                            | Some parent when
                                                AttachmentAccess.read
                                                    services.Principal
                                                    view.Actor
                                                    parent.Snapshot
                                                |> Result.isOk
                                                ->
                                                read tx view parent
                                            | _ ->
                                                System.Threading.Tasks.Task.FromResult
                                                    QueryResults.unavailable

                                        return capture view outcome
                                    }
                        }

                return! finalize services token result
        }

    let private observe (services: ServiceContext) kind attachment (view: CaseReadFacts) outcome =
        DisclosureReads.observe
            services.Principal
            view.Actor
            kind
            (view.Case |> Option.map _.CaseId)
            attachment
            outcome

    let page (services: ServiceContext) target token =
        withReadable
            services
            DisclosureReads.complete
            RequestRefusals.protectedRead
            target
            token
            (fun view outcome ->
                observe services DisclosureKind.AttachmentList None view outcome
                |> DisclosureReads.withPage (fun (page: AttachmentPage) ->
                    DisclosureReads.rows page.Items))
            (fun tx view parent ->
                task {
                    let! changes = AttachmentStorage.list tx parent.CaseId
                    let! basis = disclosure tx parent.CaseId

                    let! authors =
                        changes
                        |> List.map (_.Stamp.Authority >> ProjectionRows.familyActor)
                        |> List.distinct
                        |> PeopleReads.actorRefs tx

                    let canEdit =
                        CaseWorkPolicy.canEdit
                            services.Principal
                            view.Actor
                            view.Maintenance.Active
                            parent.Snapshot

                    let rows =
                        changes
                        |> List.map (fun change ->
                            let author = change.Stamp.Authority |> ProjectionRows.familyActor

                            match
                                change.Value,
                                AttachmentId.create change.PartId,
                                Map.tryFind author authors
                            with
                            | PartValue.Attachment(AttachmentEffect.Added(_, Some descriptor)),
                              Some attachment,
                              Some author ->
                                Ok
                                    {
                                        AttachmentRow.AttachmentId = attachment
                                        Name = AttachmentIntent.name descriptor.Intent
                                        Length = AttachmentIntent.length descriptor.Intent
                                        RecordedAt = change.Stamp.RecordedAt
                                        RecordedBy = author
                                        CanWithdraw = canEdit
                                        CanPreview = descriptor.Preview.IsSome
                                    }
                            | _ -> Error CoreFault.StoreIntegrityError)
                        |> QueryResults.collect

                    return
                        rows
                        |> Result.map (fun rows ->
                            {
                                AttachmentPage.Basis = ServiceContext.basis services tx.ObservedAt
                                CaseId = parent.CaseId
                                Reference = parent.Snapshot.Reference
                                Disclosure = basis
                                Items = rows
                                CanAdd = canEdit
                            })
                        |> Outcome.ofResult
                })

    let private descriptor
        (services: ServiceContext)
        (tx: ReadTx)
        (view: CaseReadFacts)
        (parent: StoredCase)
        attachmentId
        =
        task {
            let! current = AttachmentStorage.current tx parent.CaseId attachmentId

            return
                AttachmentAccess.current
                    services.Principal
                    view.Actor
                    parent.Snapshot
                    (current
                     |> Option.bind (fun change ->
                         match change.Value with
                         | PartValue.Attachment value -> Some value
                         | _ -> None))
        }

    let content preview (services: ServiceContext) (request: AttachmentReadRequest) token =
        let kind =
            if preview then
                DisclosureKind.AttachmentPreview
            else
                DisclosureKind.AttachmentDownload

        let capture view outcome =
            observe services kind (Some request.AttachmentId) view outcome

        task {
            // Preparing verified bytes does not authorize their release.
            let! prepared =
                withReadable
                    services
                    DisclosureReads.completePreparation
                    RequestRefusals.read
                    request.Target
                    token
                    (fun view outcome ->
                        match outcome with
                        | Outcome.Succeeded _ -> outcome, None
                        | _ -> capture view outcome)
                    (fun tx view parent ->
                        task {
                            match! descriptor services tx view parent request.AttachmentId with
                            | Error refusal -> return Outcome.Rejected refusal
                            | Ok descriptor when preview && descriptor.Preview.IsNone ->
                                return Outcome.Rejected Refusal.AttachmentPreviewUnavailable
                            | Ok descriptor ->
                                let! bytes = AttachmentStorage.read tx parent.CaseId descriptor

                                let medium = if preview then descriptor.Preview else None

                                if
                                    preview
                                    && AttachmentPreviewProfile.forName
                                        (AttachmentIntent.name descriptor.Intent)
                                        (VerifiedAttachment.bytes bytes)
                                       <> medium
                                then
                                    return Outcome.Failed CoreFault.StoreIntegrityError
                                else
                                    return
                                        Outcome.Succeeded(
                                            parent.CaseId,
                                            view.Actor.Actor |> Option.map _.ActorId,
                                            descriptor,
                                            { Bytes = bytes; Preview = medium }
                                        )
                        })

            match prepared with
            | Outcome.Failed fault -> return DisclosureReads.safeOutcome (Outcome.Failed fault)
            | Outcome.Rejected refusal ->
                return DisclosureReads.safeOutcome (Outcome.Rejected refusal)
            | Outcome.Succeeded(caseId, requester, expected, content) ->
                return!
                    withReadable
                        services
                        DisclosureReads.complete
                        RequestRefusals.protectedRead
                        (CaseSelector.Id caseId)
                        token
                        capture
                        (fun tx view parent ->
                            task {
                                match! descriptor services tx view parent request.AttachmentId with
                                | Ok current when
                                    current = expected
                                    && requester.IsSome
                                    && requester = (view.Actor.Actor |> Option.map _.ActorId)
                                    ->
                                    return Outcome.Succeeded content
                                | _ -> return QueryResults.unavailable
                            })
        }
