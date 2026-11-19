namespace ClaimCore.Application.Reads

open System
open System.Text
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.Application.Execution
open ClaimCore.RecordFormat

/// Conflicts and ordinary history share the same accepted-version projection and disclosure.
module internal TimelineHistory =
    let binding parent actor filter =
        [
            CaseId.format parent
            ActorId.format actor
            filter |> Option.map TimelineFilter.token |> Option.defaultValue ""
        ]
        |> String.concat "\u001f"
        |> Encoding.UTF8.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower

    let belongs parent filter (entry: StoredEffect) =
        entry.Change.Aggregate = AggregateReference.Family parent
        && (filter
            |> Option.forall (fun filter ->
                List.contains entry.Change.Kind (TimelineFilter.parts filter)))

    let private inRange parent (range: ChangeRange) (entry: StoredEffect) =
        entry.Change.Kind = ChangeEntity.partKind range.Entity
        && entry.Change.PartId = ChangeEntity.partId parent range.Entity
        && entry.Change.Revision > range.ExpectedRevision
        && entry.Change.Revision <= range.ActualRevision

    let validPage parent filter range before limit (page: Page<StoredEffect, EffectPosition>) =
        page.Items.Length <= limit
        && (page.Items
            |> List.pairwise
            |> List.forall (fun (first, second) -> first.Position > second.Position))
        && (page.Next
            |> Option.forall (fun position ->
                page.Items
                |> List.tryLast
                |> Option.exists (fun entry -> entry.Position = position)))
        && (page.Items
            |> List.forall (fun entry ->
                belongs parent filter entry
                && (range |> Option.forall (fun range -> inRange parent range entry))
                && (before |> Option.forall (fun before -> entry.Position < before))))

    let entries
        _services
        (tx: ReadTx)
        reference
        erased
        full
        _range
        (page: Page<StoredEffect, EffectPosition>)
        =
        task {
            let mutable authors = Map.empty

            for id in
                page.Items
                |> List.map (fun entry -> ProjectionRows.familyActor entry.Authority)
                |> List.distinct do
                let! person = AggregateStorage.person tx id

                match person with
                | None -> raise (RecordCorruptException "Timeline author is absent.")
                | Some person -> authors <- Map.add id (Actor.currentReference person.Actor) authors

            let attachmentIds =
                if full && not erased then
                    page.Items
                    |> List.choose (fun entry ->
                        if entry.Change.Kind = PartKind.Attachment then
                            AttachmentId.create entry.Change.PartId
                        else
                            None)
                    |> List.distinct
                else
                    []

            let! currentAttachments =
                match page.Items with
                | first :: _ ->
                    match first.Change.Aggregate with
                    | AggregateReference.Family id ->
                        AttachmentStorage.currentMany tx id attachmentIds
                    | AggregateReference.Person _ ->
                        invalidOp "Case timeline contains a person event."
                | _ -> System.Threading.Tasks.Task.FromResult Map.empty

            let mutable roots = Map.empty
            let result = ResizeArray<CaseTimelineEntry>()

            for entry in page.Items do
                let! changes =
                    task {
                        if erased then
                            return Ok TimelineChanges.Erased
                        elif not full then
                            return Ok TimelineChanges.Withheld
                        elif
                            entry.Change.Kind = PartKind.Attachment
                            && (AttachmentId.create entry.Change.PartId
                                |> Option.bind (fun id -> Map.tryFind id currentAttachments)
                                |> Option.forall (fun change ->
                                    match change.Value with
                                    | PartValue.Attachment(AttachmentEffect.Added(_, Some _)) ->
                                        false
                                    | _ -> true))
                        then
                            return Ok TimelineChanges.Withheld
                        elif StoredEffect.privateUnavailable entry then
                            return Error CoreFault.StoreIntegrityError
                        else
                            let! descriptions =
                                task {
                                    match Map.tryFind entry.OperationId roots with
                                    | Some descriptions -> return Ok descriptions
                                    | None ->
                                        let mutable previous = Map.empty

                                        for change in entry.Operation.Changes do
                                            let! prior =
                                                OperationStorage.previous
                                                    tx.Connection
                                                    tx.InstallationId
                                                    change
                                                    tx.CancellationToken

                                            match prior with
                                            | Some prior ->
                                                previous <-
                                                    Map.add
                                                        (prior.Kind, prior.PartId)
                                                        prior
                                                        previous
                                            | None -> ()

                                        let! basisReference =
                                            match
                                                entry.Operation.Changes
                                                |> List.tryPick (fun change ->
                                                    match change.Value with
                                                    | PartValue.Case { Reference = Some _ } when
                                                        change.Revision > Revision.first
                                                        ->
                                                        Some change.Revision
                                                    | _ -> None)
                                            with
                                            | Some revision ->
                                                match entry.Change.Aggregate with
                                                | AggregateReference.Family id ->
                                                    CaseIdentityReads.referenceBefore tx id revision
                                                | _ -> invalidOp "Expected family operation."
                                            | None -> System.Threading.Tasks.Task.FromResult None

                                        let described =
                                            PartDescriptions.familyVersions
                                                (defaultArg basisReference reference)
                                                previous
                                                entry.Operation.Changes
                                            |> Result.mapError (fun _ ->
                                                CoreFault.StoreIntegrityError)

                                        match described with
                                        | Ok descriptions ->
                                            roots <- Map.add entry.OperationId descriptions roots
                                        | Error _ -> ()

                                        return described
                                }

                            match descriptions with
                            | Error fault -> return Error fault
                            | Ok descriptions ->
                                match List.tryItem entry.Index descriptions with
                                | None -> return Error CoreFault.StoreIntegrityError
                                | Some description ->
                                    let! references =
                                        ChangeReferenceReads.read
                                            tx
                                            entry.Change.Aggregate
                                            [ description ]

                                    return
                                        PartDescriptions.timeline
                                            (references.Actors
                                             |> List.map (fun actor -> actor.ActorId, actor)
                                             |> Map.ofList)
                                            description
                                        |> Result.map TimelineChanges.Disclosed
                                        |> Result.mapError (fun _ -> CoreFault.StoreIntegrityError)
                    }

                match changes with
                | Error _ ->
                    raise (RecordCorruptException "Timeline accepted changes cannot be described.")
                | Ok changes ->
                    result.Add
                        {
                            OperationId = entry.OperationId
                            Position = entry.Position
                            OperationHash = entry.Operation.Root.Hash
                            Task = entry.Task
                            Subject =
                                {
                                    PartKind = entry.Change.Kind
                                    PartId = entry.Change.PartId
                                    Revision = entry.Change.Revision
                                }
                            RecordedAt = entry.RecordedAt
                            RecordedBy =
                                Map.find (ProjectionRows.familyActor entry.Authority) authors
                            Changes = changes
                        }

            return Ok(List.ofSeq result)
        }
