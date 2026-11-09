namespace ClaimCore.Presentation

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type Prominence =
    | Primary
    | Secondary
    | Detail

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Prominence =
    let vocabulary =
        Vocabulary.ofUnion<Prominence> "prominence" TokenCasing.ScreamingSnake None
