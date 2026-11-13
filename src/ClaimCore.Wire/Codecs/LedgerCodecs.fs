namespace ClaimCore.Wire

open ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal LedgerCodecs =
    open Codec
    open ResponseScalars

    let hash =
        digest
        |> map
            (Convert.FromHexString >> EntryHash.ofBytes >> Option.get)
            (EntryHash.toBytes >> Convert.ToHexStringLower)

    let effectPosition =
        (record (fun sequence index -> EffectPosition.create sequence index |> Option.get)
        : Fields<EffectPosition, _>)
        |> field "sequence" EffectPosition.sequence sequence
        |> field
            "effectIndex"
            EffectPosition.index
            (integer 0L (int64 Int32.MaxValue) |> map int int64)
        |> build
        |> named "EffectPosition"

    let effectId =
        (record (fun operation index -> EffectId.create operation index |> Option.get)
        : Fields<EffectId, _>)
        |> field "operationId" EffectId.operation operationId
        |> field "effectIndex" EffectId.index (integer 0L (int64 Int32.MaxValue) |> map int int64)
        |> build
        |> named "EffectId"

    let position =
        (record (fun sequence hash ->
            {
                Sequence = sequence
                EntryHash = hash
            })
        : Fields<LedgerPosition, _>)
        |> field "sequence" _.Sequence sequence
        |> field "entryHash" _.EntryHash hash
        |> build
        |> named "Position"

    let lifecycleChange =
        (record (fun before after ->
            {
                LifecycleChange.Before = before
                After = after
            })
        : Fields<LifecycleChange, _>)
        |> field "before" _.Before (nullable (VocabularyCodec.create Lifecycle.vocabulary))
        |> field "after" _.After (VocabularyCodec.create Lifecycle.vocabulary)
        |> build
        |> named "LifecycleChange"
