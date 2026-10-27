namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type Role =
    | Owner
    | CaseReader
    | CaseRegistrar
    | CaseHandler
    | PaymentRecorder
    | DataSteward
    | Auditor

module RoleTokens =
    let vocabulary = Vocabulary.ofUnion<Role> "token" TokenCasing.ScreamingSnake None

    let definitions = Vocabulary.entries vocabulary
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
