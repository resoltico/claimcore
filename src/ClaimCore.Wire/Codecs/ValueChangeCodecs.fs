namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Json

module ValueChangeCodecs =
    open Codec

    let change name value =
        (record (fun before after ->
            {
                ValueChange.Before = before
                After = after
            })
        : Fields<ValueChange<_>, _>)
        |> field "before" _.Before (nullable value)
        |> field "after" _.After (nullable value)
        |> build
        |> named name
