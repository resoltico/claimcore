namespace ClaimCore.Protocol

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type ActSubject =
    | Case
    | Authority

/// The complete act vocabulary combines the disjoint case and authority subjects.
[<RequireQualifiedAccess>]
type ActKind =
    | Case of CaseActKind
    | Authority of AuthorityKind

[<RequireQualifiedAccess>]
type ScopeKind =
    | Installation
    | Case

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ScopeKind =
    let vocabulary =
        Vocabulary.ofUnion<ScopeKind> "scope" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

/// Caller-authored scope; accepted grants resolve case references to case IDs.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActSubject =
    let vocabulary =
        Vocabulary.ofUnion<ActSubject> "subject" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActKind =
    let subject =
        function
        | ActKind.Case _ -> ActSubject.Case
        | ActKind.Authority _ -> ActSubject.Authority

    let vocabulary =
        [
            yield!
                Vocabulary.entries CaseActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Case value, token)
            yield!
                Vocabulary.entries AuthorityKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Authority value, token)
        ]
        |> Vocabulary.ofEntries "act"

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
