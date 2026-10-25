namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type ExecutionPhase =
    | NotStarted
    | StartedUnconfirmed

module DeliveryTokens =
    let vocabulary =
        [
            ExecutionPhase.NotStarted, "NOT_STARTED"
            ExecutionPhase.StartedUnconfirmed, "STARTED_UNCONFIRMED"
        ]
        |> Vocabulary.define "phase"

    let phases = Vocabulary.entries vocabulary
    let phase = Vocabulary.token vocabulary
