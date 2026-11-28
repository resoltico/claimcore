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

    let sealCursor (tx: ReadTx) actor scope range position =
        let canonical = Cursors.encodeTimeline tx.InstallationId scope range position
        let payload = System.Buffers.Text.Base64Url.DecodeFromChars(canonical.AsSpan())
        tx.Cipher.SealCursor("case-timeline", ActorId.value actor, payload)

    let openCursor (tx: ReadTx) actor scope (token: string) =
        if token.Length > Cursors.MaximumTokenCharacters then
            Error Refusal.InvalidCursor
        else
            match tx.Cipher.OpenCursor("case-timeline", ActorId.value actor, token) with
            | Some payload ->
                let canonical = System.Buffers.Text.Base64Url.EncodeToString payload
                Cursors.decodeTimeline tx.InstallationId scope canonical
            | None -> Error Refusal.InvalidCursor

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
        (services: ServiceContext)
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
                let! person = PersonAcquisition.person tx id

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
                        elif
                            entry.Change.Kind = PartKind.Link
                            && (entry.Operation.Root.Structure.Compartments
                                |> List.exists _.RelationshipCleared)
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
                                                    tx.Cipher
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

                                        let described =
                                            PartDescriptions.familyVersions
                                                reference
                                                previous
                                                entry.Operation.Changes
                                            |> Result.mapError (fun _ ->
                                                CoreFault.StoreIntegrityError)

                                        match described with
                                        | Error _ -> return described
                                        | Ok descriptions ->
                                            let targets =
                                                descriptions
                                                |> List.collect (fun part ->
                                                    match part.Description with
                                                    | PartDescription.Link link ->
                                                        [
                                                            yield! Option.toList link.Before
                                                            yield! Option.toList link.After
                                                        ]
                                                        |> List.map _.Target
                                                    | _ -> [])
                                                |> List.distinct

                                            let! allowed =
                                                task {
                                                    if targets.IsEmpty then
                                                        return Set.empty
                                                    else
                                                        let! scope =
                                                            AccessReads.accessCases
                                                                tx
                                                                (services.Principal, targets)

                                                        let visible =
                                                            targets
                                                            |> List.filter (fun id ->
                                                                Access.can
                                                                    services.Principal
                                                                    scope
                                                                    Capability.ReadCase
                                                                    (Scope.Case id))

                                                        return!
                                                            CaseLinkStorage.liveTargets tx visible
                                                }

                                            let redact (content: RelatedCaseContent option) =
                                                content
                                                |> Option.filter (fun related ->
                                                    Set.contains related.Target allowed)

                                            let descriptions =
                                                descriptions
                                                |> List.map (fun part ->
                                                    match part.Description with
                                                    | PartDescription.Link link ->
                                                        { part with
                                                            Description =
                                                                PartDescription.Link
                                                                    {
                                                                        Before = redact link.Before
                                                                        After = redact link.After
                                                                    }
                                                        }
                                                    | _ -> part)

                                            roots <- Map.add entry.OperationId descriptions roots
                                            return Ok descriptions
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
