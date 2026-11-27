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
        DefaultText: string option
        ChoicesFrom: string option
        ChoicesWhen: TaskInputChoicesWhen list
    }

[<RequireQualifiedAccess>]
type TaskRowConstraint = AtLeastOne of members: TaskInputId list * missing: Violation

type TaskCollectionDeclaration =
    {
        Collection: TaskCollectionId
        MinimumRows: int
        Inputs: TaskInputDeclaration list
        RowConstraints: TaskRowConstraint list
    }

module TaskInputDeclaration =
    let lookups =
        Map.ofList
            [
                TaskInputId.Target(InputTarget.Field BusinessField.ResponsibleActor),
                "case.assignees"
                TaskInputId.Target InputTarget.PaymentPayee, "case.parties"
            ]

    let create mode input =
        let source =
            match input with
            | TaskInputId.Target InputTarget.OperationId -> InputSource.OperationIdentity
            | TaskInputId.Target(InputTarget.ExpectedFamilySequence | InputTarget.ExpectedRevision | InputTarget.ExpectedWorkRevision | InputTarget.ExpectedReminderRevision | InputTarget.ExpectedPartyRevision) ->
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
            DefaultText = None
            ChoicesFrom = Map.tryFind input lookups
            ChoicesWhen = []
            Atom =
                if
                    source = InputSource.ExpectedRevision
                    || input = TaskInputId.Target InputTarget.Limit
                then
                    TaskAtom.Integer
                elif
                    input = TaskInputId.IncludeInactive
                    || input = TaskInputId.SettleOnly
                    || input = TaskInputId.ConfirmRetirement
                then
                    TaskAtom.Boolean
                else
                    TaskAtom.Text
        }
