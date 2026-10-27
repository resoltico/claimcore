namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type Capability =
    | ReadCase
    | ReadHistory
    | ReadHistoryFull
    | Register
    | Decide
    | RecordPayment
    | Close
    | Steward
    | ManageAuthority
    | ReadAuthority

module Capabilities =
    let vocabulary =
        Vocabulary.ofUnion<Capability> "capability" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
