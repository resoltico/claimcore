namespace ClaimCore.Domain

/// Independent input checks accumulate; state/transition refusals remain one refusal.
module Admission =
    let private merge left right =
        match left, right with
        | Refusal.InvalidInput first, Refusal.InvalidInput last ->
            Refusal.InvalidInput(InputIssues.append first last)
        | Refusal.InvalidInput _, state -> state
        | state, _ -> state

    let map2 combine left right =
        match left, right with
        | Ok left, Ok right -> Ok(combine left right)
        | Error left, Error right -> Error(merge left right)
        | Error error, _
        | _, Error error -> Error error

    let apply argument fn =
        map2 (fun fn argument -> fn argument) fn argument

    let collect checks =
        checks
        |> List.fold (fun result check -> map2 (fun () () -> ()) result check) (Ok())
