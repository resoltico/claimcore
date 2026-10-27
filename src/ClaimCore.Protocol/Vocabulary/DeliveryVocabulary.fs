namespace ClaimCore.Protocol

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ExecutionPhase =
    | NotStarted
    | StartedUnconfirmed

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ExecutionPhase =
    let vocabulary =
        Vocabulary.ofUnion<ExecutionPhase> "phase" TokenCasing.ScreamingSnake None

    let phases = Vocabulary.entries vocabulary
    let phase = Vocabulary.token vocabulary
