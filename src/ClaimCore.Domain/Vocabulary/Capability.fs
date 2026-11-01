namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type Capability =
    | ReadCase
    | ReadHistory
    | ReadHistoryFull
    | ManageCaseWork
    | Register
    | Decide
    | RecordPayment
    | Close
    | Steward
    | ManageAuthority
    | ReadAuthority

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Capability =
    let vocabulary =
        Vocabulary.ofUnion<Capability> "capability" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
