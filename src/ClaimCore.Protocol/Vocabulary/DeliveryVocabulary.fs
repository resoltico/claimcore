namespace ClaimCore.Protocol

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ExecutionPhase =
    | NotStarted
    | StartedUnconfirmed

module DeliveryTokens =
    let vocabulary =
        Vocabulary.ofUnion<ExecutionPhase> "phase" TokenCasing.ScreamingSnake None

    let phases = Vocabulary.entries vocabulary
    let phase = Vocabulary.token vocabulary
