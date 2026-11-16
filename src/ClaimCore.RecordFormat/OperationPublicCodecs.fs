namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Json

/// Closed retained structure; part value grammar is owned by its codec pair.
module OperationPublicCodecs =
    open Codec
    open RecordScalars

    let task =
        text
        |> interpret
            (fun path value ->
                TaskId.tryParse value
                |> Option.map Ok
                |> Option.defaultValue (
                    Error
                        {
                            Path = path
                            Reason = "Invalid task token."
                        }
                ))
            TaskId.token
        |> named "TaskToken"

    let structure =
        (record (fun binding retained -> binding, retained): Fields<Binding * InputMap, _>)
        |> field "binding" fst BindingCodecs.binding
        |> field "retained" snd InputMapCodecs.stored
        |> build

    let descriptor =
        (record CompartmentDescriptor.create: Fields<CompartmentDescriptor, _>)
        |> field "ordinal" _.Ordinal (integer 0L 1L |> map int int64)
        |> field "owner" _.Owner PartCodecs.owner
        |> buildResult (fun path _ ->
            {
                Path = path
                Reason = "Invalid compartment descriptor."
            })

    let private partIdentity kind =
        (record (fun _ id -> id): Fields<System.Guid, _>)
        |> field "kind" (fun _ -> kind) (VocabularyCodec.select ((=) kind) PartKind.vocabulary)
        |> field "id" id uuid
        |> build

    let effect (header: OperationHeader) index =
        let codec kind =
            (record
                (fun
                    aggregate
                    identity
                    revision
                    naturalKey
                    retained
                    erases
                    (compartment: int option) ->
                    let retained =
                        retained
                        |> Option.orElse (
                            if kind = PartKind.Work then
                                Some(PartValue.Work None)
                            else
                                None
                        )

                    match retained, EffectPosition.create header.Sequence index with
                    | Some retained, Some position ->
                        let value = PartCodecs.withCompartment compartment.IsSome retained

                        Ok
                            {
                                RecordedPart.Change =
                                    {
                                        Aggregate = aggregate
                                        PartId = identity
                                        Revision = revision
                                        NaturalKey = naturalKey
                                        Value = value
                                        Erases = erases
                                        Stamp =
                                            {
                                                Position = position
                                                OperationId = header.OperationId
                                                ActorId = header.ActorId
                                                RecordedAt = header.RecordedAt
                                            }
                                    }
                                Compartment = compartment
                            }
                    | _ -> Error "Missing retained part value or invalid position.")
            : Fields<RecordedPart, _>)
            |> field "aggregate" (_.Change >> _.Aggregate) PartCodecs.aggregate
            |> field "part" (_.Change >> _.PartId) (partIdentity kind)
            |> field "revision" (_.Change >> _.Revision) (revision 1L)
            |> optionalField "naturalKey" (_.Change >> _.NaturalKey) text
            |> optionalField
                "retained"
                (fun value ->
                    if kind = PartKind.Work then
                        None
                    else
                        Some value.Change.Value)
                (PartCodecs.retained kind)
            |> optionalField "erases" (_.Change >> _.Erases) PartCodecs.owner
            |> optionalField "compartment" _.Compartment (integer 0L 1L |> map int int64)
            |> buildResult (fun path reason -> { Path = path; Reason = reason })
            |> named ("PartEffect." + PartKind.token kind)

        Vocabulary.all PartKind.vocabulary
        |> List.map (fun kind ->
            branch
                id
                (fun (value: RecordedPart) ->
                    if value.Change.Kind = kind then Some value else None)
                (codec kind))
        |> choice "PartEffect"
