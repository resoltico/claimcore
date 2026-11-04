namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type ActSubject =
    | Case
    | Authority
    | Work
    | Reminder

/// The complete act vocabulary combines the disjoint case and authority subjects.
[<RequireQualifiedAccess>]
type ActKind =
    | Case of CaseActKind
    | Authority of AuthorityKind
    | Work of WorkActKind
    | Reminder of ReminderActKind

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
        | ActKind.Work _ -> ActSubject.Work
        | ActKind.Reminder _ -> ActSubject.Reminder

    let vocabulary =
        [
            yield!
                Vocabulary.entries CaseActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Case value, token)
            yield!
                Vocabulary.entries AuthorityKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Authority value, token)
            yield!
                Vocabulary.entries WorkActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Work value, token)
            yield!
                Vocabulary.entries ReminderActKind.vocabulary
                |> List.map (fun (value, token) -> ActKind.Reminder value, "REMINDER_" + token)
        ]
        |> Vocabulary.ofEntries "act"

    let ofInput =
        function
        | OperationInput.Case value -> ActKind.Case(CaseAct.kind value.Act)
        | OperationInput.Work _ -> ActKind.Work WorkActKind.UpdateWork
        | OperationInput.Reminder value -> ActKind.Reminder(AuthoredReminderAct.kind value.Act)
        | OperationInput.Authority value -> ActKind.Authority(AuthorityAct.kind value.Act)

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

/// A wire choice has no KEEP token: optional presence is the transport's preservation form.
[<RequireQualifiedAccess>]
type WorkChoice =
    | Set
    | Clear

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module WorkChoice =
    let vocabulary =
        Vocabulary.ofUnion<WorkChoice> "workChange" TokenCasing.ScreamingSnake None
