namespace ClaimCore.RecordFormat

open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Json

[<RequireQualifiedAccess>]
type OperationRootProblem =
    | Structure of OperationStructureProblem
    | PublicMetadata of DecodeProblem
    | EffectOrdinals
    | Hash

type OperationRoot =
    private
        {
            StructureValue: OperationStructure
            HashValue: EntryHash
        }

    member this.Structure = this.StructureValue
    member this.Header = this.StructureValue.Header
    member this.Hash = this.HashValue
    member this.Position = LedgerPosition.create this.Header.Sequence this.HashValue

module OperationRoot =
    [<Literal>]
    let MaximumPublicMetadataBytes = 65536

    let structureValue (root: OperationRoot) =
        OperationPublicCodecs.structure.Write(root.Structure.Binding, root.Structure.Retained)

    let structureBytes root =
        structureValue root |> CanonicalWriter.bytes

    let effectValues (root: OperationRoot) =
        root.Structure.Effects
        |> List.mapi (fun index effect ->
            index, (OperationPublicCodecs.effect root.Header index).Write effect)

    let effectBytes root =
        effectValues root
        |> List.map (fun (index, value) -> index, CanonicalWriter.bytes value)

    let private bytes (value: OperationStructure) =
        let header = value.Header

        Array.concat
            [
                OperationFrames.text "ClaimCore.Operation"
                OperationFrames.u32 (uint32 InstallationFormat.Current)
                OperationFrames.text InstallationFormat.Identity
                OperationFrames.uuid header.InstallationId
                OperationFrames.i64 (Sequence.value header.Sequence)
                EntryHash.toBytes header.PreviousHash
                OperationFrames.uuid (OperationId.value header.OperationId)
                (match header.Authority with
                 | Authority.Actor actor ->
                     Array.append [| 1uy |] (OperationFrames.uuid (ActorId.value actor))
                 | Authority.Operator -> [| 2uy |])
                OperationFrames.i64 header.RecordedAt.UtcTicks
                (match header.BusinessDate with
                 | None -> [| 0uy |]
                 | Some date -> Array.append [| 1uy |] (OperationFrames.u32 (uint32 date.DayNumber)))
                OperationFrames.text (TaskId.token value.Task)
                BindingCodecs.binding.Write value.Binding
                |> CanonicalWriter.bytes
                |> OperationFrames.frame
                InputMapCodecs.stored.Write value.Retained
                |> CanonicalWriter.bytes
                |> OperationFrames.frame
                OperationFrames.u32 (uint32 value.Effects.Length)
                value.Effects
                |> List.mapi (fun index effect ->
                    Array.append
                        (OperationFrames.u32 (uint32 index))
                        ((OperationPublicCodecs.effect header index).Write effect
                         |> CanonicalWriter.bytes
                         |> OperationFrames.frame))
                |> Array.concat
                OperationFrames.u32 (uint32 value.Compartments.Length)
                value.Compartments
                |> List.map (fun compartment ->
                    Array.append
                        (CompartmentDescriptor.bytes compartment.Descriptor
                         |> OperationFrames.frame)
                        compartment.Commitment)
                |> Array.concat
            ]

    let private bind structure =
        {
            StructureValue = structure
            HashValue = bytes structure |> SHA256.HashData |> EntryHash.ofBytes |> Option.get
        }

    let private declared (structure: OperationStructure) =
        match ClaimCore.Domain.Tasks.Registry.findDeclaration structure.Task with
        | None -> Ok()
        | Some declaration ->
            let aggregate =
                match structure.Effects.Head.Change.Aggregate with
                | AggregateReference.Family _ -> AggregateKind.Family
                | AggregateReference.Person _ -> AggregateKind.Person

            if aggregate <> declaration.Aggregate then
                Error(OperationRootProblem.Structure OperationStructureProblem.Task)
            elif not (BindingCodecs.matches declaration structure.Binding) then
                Error(OperationRootProblem.Structure OperationStructureProblem.Binding)
            elif
                InputMapCodecs.partition InputRetention.Retained declaration structure.Retained
                <> structure.Retained
            then
                Error(OperationRootProblem.Structure OperationStructureProblem.Inputs)
            elif InputMapCodecs.hasErasable declaration && structure.Compartments.IsEmpty then
                Error(OperationRootProblem.Structure OperationStructureProblem.Compartments)
            else
                Ok()

    let append header task binding retained effects compartments =
        OperationStructure.append header task binding retained effects compartments
        |> Result.mapError OperationRootProblem.Structure
        |> Result.bind (fun structure ->
            declared structure
            |> Result.bind (fun () ->
                let publicBytes =
                    OperationPublicCodecs.structure.Write(structure.Binding, structure.Retained)
                    |> CanonicalWriter.bytes

                let effectsTooLarge =
                    structure.Effects
                    |> List.mapi (fun index effect ->
                        (OperationPublicCodecs.effect header index).Write effect
                        |> CanonicalWriter.bytes)
                    |> List.exists (fun bytes -> bytes.Length > MaximumPublicMetadataBytes)

                if publicBytes.Length > MaximumPublicMetadataBytes || effectsTooLarge then
                    Error(
                        OperationRootProblem.PublicMetadata
                            {
                                Field = "$"
                                Message = "Public metadata exceeds the supported byte bound."
                            }
                    )
                else
                    Ok(bind structure)))

    let private read codec bytes =
        Codec.decode MaximumPublicMetadataBytes codec bytes
        |> Result.mapError (fun problem ->
            OperationRootProblem.PublicMetadata
                {
                    Field = problem.Path
                    Message = problem.Reason
                })

    let restore
        (header: OperationHeader)
        taskToken
        structureBytes
        (effectRows: (int * byte array) list)
        compartments
        expectedHash
        =
        if header.Sequence < Sequence.first then
            Error(OperationRootProblem.Structure OperationStructureProblem.Header)
        elif
            effectRows.IsEmpty
            || effectRows.Length > OperationStructure.MaximumEffects
            || (effectRows |> List.map fst) <> [ 0 .. effectRows.Length - 1 ]
        then
            Error OperationRootProblem.EffectOrdinals
        else
            OperationPublicCodecs.task.Read "$.task" (JsonValue.String taskToken)
            |> Result.mapError (fun problem ->
                OperationRootProblem.PublicMetadata
                    {
                        Field = problem.Path
                        Message = problem.Reason
                    })
            |> Result.bind (fun task ->
                read OperationPublicCodecs.structure structureBytes
                |> Result.bind (fun (binding, retained) ->
                    effectRows
                    |> List.map (fun (index, bytes) ->
                        read (OperationPublicCodecs.effect header index) bytes)
                    |> List.fold
                        (fun current next ->
                            current
                            |> Result.bind (fun current ->
                                next |> Result.map (fun next -> next :: current)))
                        (Ok [])
                    |> Result.bind (fun effects ->
                        OperationStructure.create
                            header
                            task
                            binding
                            retained
                            (List.rev effects)
                            compartments
                        |> Result.mapError OperationRootProblem.Structure)))
            |> Result.bind (fun structure ->
                declared structure |> Result.map (fun () -> bind structure))
            |> Result.bind (fun root ->
                if root.Hash = expectedHash then
                    Ok root
                else
                    Error OperationRootProblem.Hash)
