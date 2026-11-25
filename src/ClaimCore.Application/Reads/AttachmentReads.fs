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

    let private withReadable (services: ServiceContext) target token capture read =
        task {
            match CaseRecords.validateSelector target with
            | Error refusal -> return! RequestRefusals.read services token refusal
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

                return! DisclosureReads.complete services token result
        }

    let page (services: ServiceContext) target token =
        withReadable services target token (fun _ outcome -> outcome, None) (fun tx view parent ->
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

    let content preview (services: ServiceContext) (request: AttachmentReadRequest) token =
        let kind =
            if preview then
                DisclosureKind.AttachmentPreview
            else
                DisclosureKind.AttachmentDownload

        let capture (view: CaseReadFacts) outcome =
            let caseId =
                DisclosureReads.target request.Target (view.Case |> Option.map _.CaseId)

            DisclosureReads.observe
                services.Principal
                view.Actor
                kind
                caseId
                (Some request.AttachmentId)
                outcome

        withReadable services request.Target token capture (fun tx view parent ->
            task {
                let! current = AttachmentStorage.current tx parent.CaseId request.AttachmentId

                match
                    AttachmentAccess.current
                        services.Principal
                        view.Actor
                        parent.Snapshot
                        (current
                         |> Option.bind (fun change ->
                             match change.Value with
                             | PartValue.Attachment value -> Some value
                             | _ -> None))
                with
                | Error refusal -> return Outcome.Rejected refusal
                | Ok descriptor when not preview ->
                    let! bytes = AttachmentStorage.read tx parent.CaseId descriptor
                    return Outcome.Succeeded { Bytes = bytes; Preview = None }
                | Ok descriptor ->
                    match descriptor.Preview with
                    | None -> return Outcome.Rejected Refusal.AttachmentPreviewUnavailable
                    | Some kind ->
                        let! bytes = AttachmentStorage.read tx parent.CaseId descriptor

                        let actual =
                            AttachmentPreviewProfile.forName
                                (AttachmentIntent.name descriptor.Intent)
                                (VerifiedAttachment.bytes bytes)

                        if actual = descriptor.Preview then
                            return Outcome.Succeeded { Bytes = bytes; Preview = Some kind }
                        else
                            return Outcome.Failed CoreFault.StoreIntegrityError
            })
