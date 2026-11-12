namespace ClaimCore.RecordFormat

open System
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.Json

[<RequireQualifiedAccess>]
type OperationRootProblem =
    | Structure of OperationStructureProblem
    | PublicMetadata of DecodeProblem
    | EffectOrdinals
    | Hash

/// The single accepted operation hash commits its complete ordered structural closure.
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

type BoundOperationEffect =
    {
        Id: EffectId
        Position: EffectPosition
        Descriptor: EffectDescriptor
    }

module OperationRoot =
    [<Literal>]
    let MaximumPublicMetadataBytes = 65536

    let private canonical (codec: Codec<'a>) value =
        codec.Write value |> CanonicalWriter.bytes

    let commandBytes (value: OperationRoot) =
        canonical OperationPublicCodecs.command value.Structure.Command

    let effectBytes (value: OperationRoot) =
        value.Structure.Effects
        |> List.mapi (fun index effect -> index, canonical OperationPublicCodecs.effect effect)

    let private bytes (structure: OperationStructure) =
        let header = structure.Header

        Array.concat
            [
                OperationFrames.text "ClaimCore.Operation"
                OperationFrames.u32 (uint32 InstallationFormat.Current)
                OperationFrames.uuid header.InstallationId
                OperationFrames.i64 (Sequence.value header.Sequence)
                EntryHash.toBytes header.PreviousHash
                OperationFrames.uuid (OperationId.value header.OperationId)
                OperationFrames.uuid (ActorId.value header.ActorId)
                OperationFrames.i64 header.RecordedAt.UtcTicks
                match header.BusinessDate with
                | None -> [| 0uy |]
                | Some date -> Array.append [| 1uy |] (OperationFrames.u32 (uint32 date.DayNumber))
                canonical OperationPublicCodecs.command structure.Command
                |> OperationFrames.frame
                OperationFrames.u32 (uint32 structure.Effects.Length)
                structure.Effects
                |> List.mapi (fun index effect ->
                    Array.concat
                        [
                            OperationFrames.u32 (uint32 index)
                            canonical OperationPublicCodecs.effect effect |> OperationFrames.frame
                        ])
                |> Array.concat
                OperationFrames.u32 (uint32 structure.Compartments.Length)
                structure.Compartments
                |> List.map (fun content ->
                    Array.concat
                        [
                            CompartmentDescriptor.bytes content.Descriptor |> OperationFrames.frame
                            content.Commitment
                        ])
                |> Array.concat
            ]

    let private bind structure =
        let hash = bytes structure |> SHA256.HashData |> EntryHash.ofBytes |> Option.get

        {
            StructureValue = structure
            HashValue = hash
        }

    let append header command effects compartments =
        OperationStructure.append header command effects compartments
        |> Result.mapError OperationRootProblem.Structure
        |> Result.map bind

    let private read codec bytes =
        Codec.decode MaximumPublicMetadataBytes codec bytes
        |> Result.mapError (fun error ->
            OperationRootProblem.PublicMetadata
                {
                    Field = error.Path
                    Message = error.Reason
                })

    /// Public metadata may come from JSONB; property order/whitespace are not business identity.
    /// Canonical hash bytes are always derived from the interpreted closed metadata grammar.
    let restore
        header
        commandBytes
        (effectRows: (int * byte array) list)
        compartments
        expectedHash
        =
        if
            effectRows.IsEmpty
            || effectRows.Length > OperationStructure.MaximumEffects
            || (effectRows |> List.map fst) <> [ 0 .. effectRows.Length - 1 ]
        then
            Error OperationRootProblem.EffectOrdinals
        else
            read OperationPublicCodecs.command commandBytes
            |> Result.bind (fun command ->
                effectRows
                |> List.fold
                    (fun state (_, bytes) ->
                        state
                        |> Result.bind (fun effects ->
                            read OperationPublicCodecs.effect bytes
                            |> Result.map (fun effect -> effect :: effects)))
                    (Ok [])
                |> Result.bind (fun effects ->
                    OperationStructure.restore header command (List.rev effects) compartments
                    |> Result.mapError OperationRootProblem.Structure))
            |> Result.map bind
            |> Result.bind (fun root ->
                if root.Hash = expectedHash then
                    Ok root
                else
                    Error OperationRootProblem.Hash)

    let effects (root: OperationRoot) =
        root.Structure.Effects
        |> List.mapi (fun index descriptor ->
            {
                BoundOperationEffect.Id =
                    EffectId.create root.Header.OperationId index |> Option.get
                Position = EffectPosition.create root.Header.Sequence index |> Option.get
                Descriptor = descriptor
            })
