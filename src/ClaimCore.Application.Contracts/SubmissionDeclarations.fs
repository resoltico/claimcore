namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

/// Revision acquisition and admission share the same semantic bounds and creation basis.
type SubmissionRevision =
    {
        Input: InputTarget
        Minimum: int64
        Maximum: int64
        Initial: int64 option
    }

type SubmissionBindingDeclaration =
    {
        Shape: BindingShape
        Subject: ActSubject
        SelectsCase: bool
        Revision: SubmissionRevision option
        Identity: TaskInputId option
        Family: SubmissionRevision option
    }

type SubmissionScopeRule =
    {
        Scope: string option
        HasId: bool
        HasReference: bool
        Kind: string
        Input: TaskInputId option
    }

module SubmissionDeclarations =
    let creationRevision = Revision.value Revision.zero
    let existingRevision = Revision.value Revision.first
    let familyRevision = Sequence.value Sequence.first
    let scopeInput = TaskInputId.Scope
    let caseIdInput = TaskInputId.CaseId

    let caseReferenceInput =
        TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)

    let installationScope = ScopeKind.token ScopeKind.Installation
    let caseScope = ScopeKind.token ScopeKind.Case

    /// Closed acquisition states. Missing CASE text remains an incomplete reference draft.
    /// Every unlisted combination is invalid, including installation plus case selectors.
    let scopeRules =
        [
            None, false, false, "INSTALLATION", None
            Some installationScope, false, false, "INSTALLATION", None
            None, true, false, "ID", Some caseIdInput
            None, false, true, "REFERENCE", Some caseReferenceInput
            Some caseScope, true, false, "ID", Some caseIdInput
            Some caseScope, false, true, "REFERENCE", Some caseReferenceInput
            Some caseScope, false, false, "REFERENCE", Some caseReferenceInput
        ]
        |> List.map (fun (scope, hasId, hasReference, kind, input) ->
            {
                Scope = scope
                HasId = hasId
                HasReference = hasReference
                Kind = kind
                Input = input
            })

    let scopeRule scope hasId hasReference =
        scopeRules
        |> List.tryFind (fun rule ->
            rule.Scope = scope && rule.HasId = hasId && rule.HasReference = hasReference)

    let binding (declaration: TaskDeclaration) =
        let shape = TaskDeclaration.bindingShape declaration

        let revision input minimum initial =
            Some
                {
                    Input = input
                    Minimum = minimum
                    Maximum = Option.defaultValue Revision.Maximum initial
                    Initial = initial
                }

        let expected, identity, family =
            match shape with
            | BindingShape.CaseNew
            | BindingShape.PeopleNew ->
                revision InputTarget.ExpectedRevision creationRevision (Some creationRevision),
                None,
                None
            | BindingShape.Case ->
                revision InputTarget.ExpectedRevision existingRevision None, None, None
            | BindingShape.CasePayment ->
                revision InputTarget.ExpectedRevision existingRevision None,
                Some(TaskInputId.Target InputTarget.PaymentId),
                None
            | BindingShape.CaseErase ->
                revision InputTarget.ExpectedRevision existingRevision None,
                None,
                Some
                    {
                        Input = InputTarget.ExpectedFamilySequence
                        Minimum = familyRevision
                        Maximum = Sequence.Maximum
                        Initial = None
                    }
            | BindingShape.Work ->
                revision InputTarget.ExpectedWorkRevision creationRevision None, None, None
            | BindingShape.NoteNew
            | BindingShape.ReminderNew -> None, None, None
            | BindingShape.Note -> None, Some(TaskInputId.Target InputTarget.NoteId), None
            | BindingShape.Reminder ->
                revision InputTarget.ExpectedReminderRevision existingRevision None,
                Some(TaskInputId.Target InputTarget.ReminderId),
                None
            | BindingShape.People ->
                revision InputTarget.ExpectedRevision existingRevision None,
                Some TaskInputId.ActorId,
                None

        {
            Shape = shape
            Subject = declaration.Target
            SelectsCase = declaration.Target <> ActSubject.People && shape <> BindingShape.CaseNew
            Revision = expected
            Identity = identity
            Family = family
        }

    let integerBounds declaration input =
        let plan = binding declaration

        [ plan.Revision; plan.Family ]
        |> List.choose id
        |> List.tryFind (fun basis -> TaskInputId.Target basis.Input = input)
        |> Option.map (fun basis -> basis.Minimum, basis.Maximum)
        |> Option.defaultValue (-SafeInteger.Maximum, SafeInteger.Maximum)

    let fixedSeed (input: TaskInputDeclaration) =
        input.Source = InputSource.Authored && input.Mode <> TaskInputMode.Update

    /// Correction fields are authored only while their declared group is explicitly updated.
    let activation (inputs: TaskInputDeclaration list) (input: TaskInputDeclaration) =
        match input.Input with
        | TaskInputId.Target(InputTarget.Field field) ->
            (BusinessField.describe field).Group
            |> Option.bind (fun group ->
                let group = TaskInputId.Target(InputTarget.Group group)

                if inputs |> List.exists (fun declared -> declared.Input = group) then
                    Some(group, CorrectionGroupAction.token CorrectionGroupAction.Update)
                else
                    None)
        | _ -> None
