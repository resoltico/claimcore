namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type ActSubject =
    | Case
    | People
    | Work
    | Reminder
    | Note

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActSubject =
    let vocabulary =
        Vocabulary.ofUnion<ActSubject> "subject" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary
