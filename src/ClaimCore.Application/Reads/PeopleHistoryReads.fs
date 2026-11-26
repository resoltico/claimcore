namespace ClaimCore.Application.Reads

open System
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage

/// Historical rights/profile facts are accepted values; current labels are independently resolved.
module internal PeopleHistoryReads =
    let read (tx: ReadTx) (entries: StoredEffect list) =
        task {
            if entries.IsEmpty then
                return Ok []
            else
                let targets =
                    entries
                    |> List.map (fun entry ->
                        match entry.Change.Aggregate with
                        | AggregateReference.Person id -> id
                        | _ ->
                            raise (RecordCorruptException "People history contains a family part."))
                    |> List.distinct

                let through = entries |> List.map _.Sequence |> List.max

                let! rows =
                    DatabaseSession.rows
                        tx.Connection
                        "SELECT l.operation_id,e.effect_index FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence WHERE e.aggregate_kind='PERSON' AND e.aggregate_id=ANY($1::uuid[]) AND e.operation_sequence<=$2 ORDER BY e.operation_sequence,e.effect_index"
                        [|
                            box (targets |> List.map ActorId.value |> List.toArray)
                            box (Sequence.value through)
                        |]
                        tx.CancellationToken

                let! prefix = StoredEntries.bind tx.Connection tx.Cipher rows tx.CancellationToken

                let wanted = entries |> List.map _.Position |> Set.ofList

                let descriptions =
                    Collections.Generic.Dictionary<EffectPosition, PersonPartDescription>()

                let mutable people = Map.empty

                for entry in prefix do
                    if ClaimCore.Domain.Tasks.Registry.find entry.Task |> Option.isNone then
                        raise (RecordCorruptException "People history contains an unknown task.")

                    let id =
                        match entry.Change.Aggregate with
                        | AggregateReference.Person id -> id
                        | _ ->
                            raise (RecordCorruptException "People history has a foreign aggregate.")

                    let before = Map.tryFind id people

                    let after =
                        Person.apply before entry.Change
                        |> Result.defaultWith (fun _ ->
                            raise (
                                RecordCorruptException
                                    "People history has an invalid accepted version."
                            ))

                    if Set.contains entry.Position wanted then
                        let description =
                            PersonDescription.fromChange before entry.Change
                            |> Result.defaultWith (fun _ ->
                                raise (
                                    RecordCorruptException
                                        "People history cannot describe its accepted value."
                                ))

                        descriptions.Add(entry.Position, description)

                    people <- Map.add id after people

                let mutable labels = Map.empty

                let ids =
                    (entries |> List.choose (_.Authority >> Authority.actorId)) @ targets
                    |> List.distinct

                for id in ids do
                    let! person = PersonAcquisition.person tx id

                    match person with
                    | None -> raise (RecordCorruptException "History account label is absent.")
                    | Some person ->
                        labels <- Map.add id (Actor.currentReference person.Actor) labels

                let result = ResizeArray<PeopleHistoryEntry>()

                for entry in entries do
                    let target =
                        match entry.Change.Aggregate with
                        | AggregateReference.Person id -> id
                        | _ ->
                            raise (RecordCorruptException "People history has a foreign aggregate.")

                    let description =
                        match descriptions.TryGetValue entry.Position with
                        | true, value -> value
                        | _ ->
                            raise (
                                RecordCorruptException
                                    "Requested history version is absent from its prefix."
                            )

                    let part =
                        {
                            DescribedPart.PartId = entry.Change.PartId
                            Revision = entry.Change.Revision
                            Description = PartDescription.Person description
                        }

                    let! references = ChangeReferenceReads.read tx entry.Change.Aggregate [ part ]

                    let recorder =
                        Authority.reference (fun id -> Map.tryFind id labels) entry.Authority
                        |> Option.defaultWith (fun () ->
                            raise (RecordCorruptException "History account label is absent."))

                    result.Add
                        {
                            OperationId = entry.OperationId
                            Position = entry.Position
                            OperationHash = entry.Operation.Root.Hash
                            RecordedAt = entry.RecordedAt
                            RecordedBy = recorder
                            Target = Map.find target labels
                            Task = entry.Task
                            Changes = description
                            PrivateAvailable = StoredEffect.privateAvailable entry
                            References = references
                        }

                return Ok(List.ofSeq result)
        }
