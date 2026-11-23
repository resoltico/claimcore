namespace ClaimCore.Domain

/// Task consequence is independent of a caller's current access.
[<RequireQualifiedAccess>]
type Consequence =
    | Routine
    | Stewardship
    | Irreversible

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Consequence =
    let vocabulary =
        Vocabulary.ofUnion<Consequence> "consequence" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type TaskReview =
    | Required
    | Implicit

module TaskReview =
    let vocabulary =
        Vocabulary.ofUnion<TaskReview> "review" TokenCasing.ScreamingSnake None
