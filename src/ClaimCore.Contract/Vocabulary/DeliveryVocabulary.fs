namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type ExecutionPhase =
    | NotStarted
    | StartedUnconfirmed

module DeliveryTokens =
    let phases =
        [
            ExecutionPhase.NotStarted, "NOT_STARTED"
            ExecutionPhase.StartedUnconfirmed, "STARTED_UNCONFIRMED"
        ]

    let phase value =
        phases |> List.find (fst >> (=) value) |> snd
