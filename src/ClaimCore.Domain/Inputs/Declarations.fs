namespace ClaimCore.Domain

[<RequireQualifiedAccess>]
type TaskInputMode =
    | Required
    | Optional
    | Update

module TaskInputMode =
    let vocabulary =
        Vocabulary.ofUnion<TaskInputMode> "inputMode" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type TaskAtom =
    | Text
    | Integer
    | Boolean

[<RequireQualifiedAccess>]
type InputRetention =
    | Retained
    | Erasable

module InputRetention =
    let vocabulary =
        Vocabulary.ofUnion<InputRetention> "inputRetention" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

type TaskInputChoicesWhen =
    {
        Input: TaskInputId
        Value: string
        Choices: string list
    }

/// Value rules remain with the referenced Domain input. This declaration owns task selection only.
type TaskInputDeclaration =
    {
        Input: TaskInputId
        Mode: TaskInputMode
        Source: InputSource
        Clearable: bool
        Atom: TaskAtom
        Multiple: bool
        Retention: InputRetention
        ChoicesFrom: string option
        ChoicesWhen: TaskInputChoicesWhen option
    }

type TaskCollectionDeclaration =
    {
        Collection: TaskCollectionId
        MinimumRows: int
        Inputs: TaskInputDeclaration list
    }

module TaskInputDeclaration =
    let create mode input =
        let source =
            match input with
            | TaskInputId.Target InputTarget.OperationId -> InputSource.OperationIdentity
            | TaskInputId.Target(InputTarget.ExpectedFamilySequence | InputTarget.ExpectedRevision | InputTarget.ExpectedWorkRevision | InputTarget.ExpectedReminderRevision) ->
                InputSource.ExpectedRevision
            | _ -> InputSource.Authored

        {
            Input = input
            Mode = mode
            Source = source
            Clearable = false
            Multiple = false
            Retention =
                match source, input with
                | (InputSource.OperationIdentity | InputSource.ExpectedRevision | InputSource.Binding),
                  _ -> InputRetention.Retained
                | _,
                  TaskInputId.Target(InputTarget.PrincipalKind | InputTarget.PrincipalIssuer | InputTarget.PrincipalSubject | InputTarget.Role | InputTarget.Scope)
                | _, TaskInputId.Scope -> InputRetention.Retained
                | _ -> InputRetention.Erasable
            ChoicesFrom =
                if input = TaskInputId.Target(InputTarget.Field BusinessField.ResponsibleActor) then
                    Some "case.assignees"
                else
                    None
            ChoicesWhen =
                if input = TaskInputId.Target InputTarget.Role then
                    Some
                        {
                            Input = TaskInputId.Scope
                            Value = ScopeKind.token ScopeKind.Case
                            Choices =
                                Access.roles
                                |> List.filter (fun role ->
                                    List.contains ScopeKind.Case (Access.roleScopes role))
                                |> List.map Role.token
                        }
                else
                    None
            Atom =
                if
                    source = InputSource.ExpectedRevision
                    || input = TaskInputId.Target InputTarget.Limit
                then
                    TaskAtom.Integer
                elif input = TaskInputId.IncludeInactive then
                    TaskAtom.Boolean
                else
                    TaskAtom.Text
        }
