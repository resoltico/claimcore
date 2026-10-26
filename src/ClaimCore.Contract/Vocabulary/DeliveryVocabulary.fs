namespace ClaimCore.Contract

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ExecutionPhase =
    | NotStarted
    | StartedUnconfirmed

module DeliveryTokens =
    let vocabulary =
        Vocabulary.define
            "phase"
            [ ExecutionPhase.NotStarted; ExecutionPhase.StartedUnconfirmed ]
            (function
             | ExecutionPhase.NotStarted -> "NOT_STARTED"
             | ExecutionPhase.StartedUnconfirmed -> "STARTED_UNCONFIRMED")

    let phases = Vocabulary.entries vocabulary
    let phase = Vocabulary.token vocabulary
