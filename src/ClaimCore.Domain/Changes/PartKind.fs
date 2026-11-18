namespace ClaimCore.Domain.Changes

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type PartKind =
    | Case
    | Payment
    | Work
    | Reminder
    | Note
    | Party
    | Person

module PartKind =
    let vocabulary = Vocabulary.ofUnion<PartKind> "part" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary
