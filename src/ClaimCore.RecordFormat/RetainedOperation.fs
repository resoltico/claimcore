namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Json

[<RequireQualifiedAccess>]
type RetainedOperationProblem =
    | Root of OperationRootProblem
    | Compartment of CompartmentProblem
    | Content of DecodeProblem
    | Inputs
    | UnknownTask

/// One validated root and its available accepted values. Missing inputs remain explicit.
type RetainedOperation =
    private
        {
            RootValue: OperationRoot
            InputsValue: InputMap option
            ChangesValue: PartChange list
        }

    member this.Root = this.RootValue
    member this.Inputs = this.InputsValue
    member this.Changes = this.ChangesValue

module RetainedOperation =
    let private contentProblem problem =
        RetainedOperationProblem.Content
            {
                Field = problem.Path
                Message = problem.Reason
            }

    let private any (inputs: InputMap) =
        not inputs.Values.IsEmpty || not inputs.Collections.IsEmpty

    let create header task binding (inputs: InputMap) (changes: PartChange list) newNonce =
        match ClaimCore.Domain.Tasks.Registry.findDeclaration task with
        | None -> Error RetainedOperationProblem.UnknownTask
        | Some _ when not (OperationStructure.validHeader header) ->
            Error(
                RetainedOperationProblem.Root(
                    OperationRootProblem.Structure OperationStructureProblem.Header
                )
            )
        | Some _ when changes.IsEmpty ->
            Error(
                RetainedOperationProblem.Root(
                    OperationRootProblem.Structure OperationStructureProblem.Effects
                )
            )
        | Some declaration ->
            let retained = InputMapCodecs.partition InputRetention.Retained declaration inputs
            let erasable = InputMapCodecs.partition InputRetention.Erasable declaration inputs

            let privateValues =
                changes
                |> List.mapi (fun index change ->
                    PartCodecs.privateValue change.PartId change.Value
                    |> Result.map (fun value -> index, value))

            (if InputMapCodecs.merge retained erasable <> Some inputs then
                 Error RetainedOperationProblem.Inputs
             else
                 Ok())
            |> Result.bind (fun () ->
                privateValues
                |> List.fold
                    (fun current next ->
                        current
                        |> Result.bind (fun values ->
                            next |> Result.map (fun value -> value :: values)))
                    (Ok [])
                |> Result.mapError RetainedOperationProblem.Content)
            |> Result.bind (fun values ->
                let values =
                    List.rev values
                    |> List.choose (fun (index, value) ->
                        value |> Option.map (fun value -> index, value))

                let hasPayload =
                    any erasable || not values.IsEmpty || InputMapCodecs.hasErasable declaration

                let compartments =
                    if not hasPayload then
                        Ok []
                    else
                        CompartmentDescriptor.create 0 (PartChange.owner changes.Head)
                        |> Result.mapError RetainedOperationProblem.Compartment
                        |> Result.bind (fun descriptor ->
                            let payload =
                                JsonValue.Object
                                    [
                                        "inputs", InputMapCodecs.stored.Write erasable
                                        "values",
                                        JsonValue.Object(
                                            values
                                            |> List.map (fun (index, value) ->
                                                string index, value)
                                        )
                                    ]
                                |> CanonicalWriter.bytes

                            Compartment.seal descriptor (newNonce ()) payload
                            |> Result.map List.singleton
                            |> Result.mapError RetainedOperationProblem.Compartment)

                compartments
                |> Result.bind (fun compartments ->
                    let effects =
                        changes
                        |> List.map (fun change ->
                            {
                                RecordedPart.Change =
                                    { change with
                                        Value = PartValue.clear change.Value
                                    }
                                Compartment =
                                    if PartCodecs.hasPrivate change.Value then Some 0 else None
                            })

                    OperationRoot.append header task binding retained effects compartments
                    |> Result.mapError RetainedOperationProblem.Root
                    |> Result.map (fun root ->
                        {
                            RootValue = root
                            InputsValue = Some inputs
                            ChangesValue = changes
                        })))

    let private payloadCodec (root: OperationRoot) ordinal =
        let fields =
            root.Structure.Effects
            |> List.mapi (fun index effect -> index, effect)
            |> List.choose (fun (index, effect) ->
                if effect.Compartment = Some ordinal then
                    Some(
                        index,
                        string index,
                        PartCodecs.privateValueCodec effect.Change.PartId effect.Change.Value
                    )
                else
                    None)

        (Codec.record (fun inputs values -> inputs, values)
        : Fields<InputMap * Map<int, PartValue>, _>)
        |> Codec.field "inputs" fst InputMapCodecs.stored
        |> Codec.field "values" snd (Codec.fieldMap fields)
        |> Codec.build

    let restore (root: OperationRoot) =
        let decoded =
            root.Structure.Compartments
            |> List.map (fun compartment ->
                match Compartment.content compartment with
                | None -> Ok(None, Map.empty)
                | Some(_, payload) ->
                    let codec = payloadCodec root compartment.Descriptor.Ordinal

                    Codec.decode Compartment.MaximumContentBytes codec payload
                    |> Result.bind (fun value ->
                        if codec.Write value |> CanonicalWriter.bytes = payload then
                            Ok value
                        else
                            Error
                                {
                                    Path = "$"
                                    Reason = "Private content is not canonical."
                                })
                    |> Result.map (fun (inputs, values) -> Some inputs, values)
                    |> Result.mapError contentProblem)

        decoded
        |> List.fold
            (fun current next ->
                current
                |> Result.bind (fun current -> next |> Result.map (fun next -> next :: current)))
            (Ok [])
        |> Result.bind (fun decoded ->
            let decoded = List.rev decoded

            let values =
                decoded
                |> List.fold
                    (fun result (_, values) ->
                        Map.fold (fun result key value -> Map.add key value result) result values)
                    Map.empty

            let changes =
                root.Structure.Effects
                |> List.mapi (fun index effect ->
                    { effect.Change with
                        Value =
                            Map.tryFind index values |> Option.defaultValue effect.Change.Value
                    })

            let privateInputs =
                decoded
                |> List.fold
                    (fun result (inputs, _) ->
                        match result, inputs with
                        | Some current, Some inputs -> InputMapCodecs.merge current inputs
                        | _ -> None)
                    (Some InputMap.empty)

            let declared = ClaimCore.Domain.Tasks.Registry.findDeclaration root.Structure.Task

            let complete =
                match declared with
                | Some declaration when not (InputMapCodecs.hasErasable declaration) ->
                    Some root.Structure.Retained
                | _ -> privateInputs |> Option.bind (InputMapCodecs.merge root.Structure.Retained)

            let privateRetentionValid =
                match declared with
                | None -> true
                | Some declaration ->
                    decoded
                    |> List.forall (fun (inputs, _) ->
                        inputs
                        |> Option.forall (fun inputs ->
                            InputMapCodecs.partition InputRetention.Erasable declaration inputs =
                                inputs))

            match complete, declared with
            | _ when not privateRetentionValid -> Error RetainedOperationProblem.Inputs
            | Some inputs, Some declaration when
                InputMapCodecs.partition InputRetention.Retained declaration inputs
                <> root.Structure.Retained
                ->
                Error RetainedOperationProblem.Inputs
            | Some inputs, Some declaration when
                InputMapCodecs.merge
                    (InputMapCodecs.partition InputRetention.Retained declaration inputs)
                    (InputMapCodecs.partition InputRetention.Erasable declaration inputs)
                <> Some inputs
                ->
                Error RetainedOperationProblem.Inputs
            | _ ->
                Ok
                    {
                        RootValue = root
                        InputsValue = complete
                        ChangesValue = changes
                    })
