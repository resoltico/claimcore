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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Role =
    let vocabulary = Vocabulary.ofUnion<Role> "role" TokenCasing.ScreamingSnake None

    let definitions = Vocabulary.entries vocabulary
    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
