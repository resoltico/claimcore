namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type ActSubject =
    | Case
    | People
    | Work
    | Reminder
    | Note
    | Party

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActSubject =
    let vocabulary =
        Vocabulary.ofUnion<ActSubject> "subject" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type ActFamily =
    | Business
    | Stewardship

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActFamily =
    let vocabulary =
        Vocabulary.ofUnion<ActFamily> "actFamily" TokenCasing.ScreamingSnake None
