namespace ClaimCore.Protocol

open System
open ClaimCore.Domain
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

    let changeSummary =
        (record (fun id position revision act at by ->
            {
                OperationId = id
                Position = position
                Revision = revision
                Act = act
                RecordedAt = at
                RecordedBy = by
            })
        : Fields<ChangeSummary, _>)
        |> field "operationId" _.OperationId operationId
        |> field "position" _.Position position
        |> field "revision" _.Revision positiveRevision
        |> field "act" _.Act (VocabularyCodec.create CaseActKind.vocabulary)
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy actorId
        |> build
        |> named "ChangeSummary"

    let fieldChange =
        (record (fun field before after ->
            {
                FieldName = field
                Before = before
                After = after
            })
        : Fields<FieldDiff, _>)
        |> field "field" _.FieldName (VocabularyCodec.create BusinessField.vocabulary)
        |> field "before" _.Before (nullable nonempty)
        |> field "after" _.After (nullable nonempty)
        |> build
        |> named "FieldChange"
