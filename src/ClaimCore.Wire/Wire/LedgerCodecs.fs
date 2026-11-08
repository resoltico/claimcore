namespace ClaimCore.Wire

open ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal LedgerCodecs =
    open Codec
    open ResponseScalars

    let position =
        (record (fun sequence hash ->
            {
                Sequence = sequence
                EntryHash = hash
            })
        : Fields<LedgerPosition, _>)
        |> field "sequence" _.Sequence sequence
        |> field
            "entryHash"
            _.EntryHash
            (digest
             |> map
                 (Convert.FromHexString >> EntryHash.ofBytes >> Option.get)
                 (EntryHash.toBytes >> Convert.ToHexStringLower))
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
