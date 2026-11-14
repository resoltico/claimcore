module ClaimCore.TestSupport.Submissions

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts

// Transport fixtures adapt typed domain scenarios; protocol assertions use literal maps independently.
let ofCommand actorId command =
    let input value = TaskInputId.Target value
    let field name = input (InputTarget.Field name)
    let text name value = name, SubmissionValue.Text value

    let fields values =
        values |> List.map (fun (name, value) -> text (field name) value)

    let updates values =
        values
        |> List.map (fun (name, value) ->
            field name,
            value
            |> Option.map SubmissionValue.Text
            |> Option.defaultValue SubmissionValue.Clear)

    let profile (value: ActorProfileInput) =
        [
            text (input InputTarget.ProfileDisplayName) value.DisplayName
            match value.Contact with
            | Some contact -> text (input InputTarget.ProfileContact) contact
            | None -> ()
        ]

    let grant (value: AuthoredGrant) =
        [
            text (input InputTarget.Role) (Role.token value.Role)
            text
                TaskInputId.Scope
                (match value.Scope with
                 | GrantTarget.Installation -> "INSTALLATION"
                 | _ -> "CASE")
            match value.Scope with
            | GrantTarget.CaseReference reference ->
                text (field BusinessField.CaseReference) reference
            | GrantTarget.CaseId id -> text TaskInputId.CaseId ((CaseId.value id).ToString("D"))
            | GrantTarget.Installation -> ()
        ]

    let task, binding, values =
        match command with
        | OperationInput.Case request ->
            let payment, values =
                match request.Act with
                | CaseAct.Open value -> None, fields (RegistrationInput.values value)
                | CaseAct.Decide value -> None, fields (DecisionInput.values value)
                | CaseAct.RecordPayment value -> None, fields (PaymentInput.values value)
                | CaseAct.AmendRegistration value ->
                    None, updates (RegistrationUpdateInput.values value)
                | CaseAct.Correct value ->
                    let registration, registrationFields =
                        match value.Registration with
                        | Amendment.Keep -> "KEEP", []
                        | Amendment.Update value ->
                            "UPDATE", updates (RegistrationUpdateInput.values value)

                    let decision, decisionFields =
                        match value.Decision with
                        | Change.Keep -> "KEEP", []
                        | Change.Clear -> "CLEAR", []
                        | Change.Update value ->
                            "UPDATE", updates (DecisionUpdateInput.values value)

                    None,
                    [
                        text (input (InputTarget.Group RecordGroup.Registration)) registration
                        text (input (InputTarget.Group RecordGroup.Decision)) decision
                    ]
                    @ registrationFields
                    @ decisionFields
                | CaseAct.RemovePayment id -> Some id, []
                | CaseAct.Void reason
                | CaseAct.Reinstate reason -> None, [ text (input InputTarget.Reason) reason ]
                | CaseAct.Bare _ -> None, []

            ActKind.Case(CaseAct.kind request.Act),
            OperationBinding.Case(request.Target, request.ExpectedRevision, payment),
            values
        | OperationInput.Work request ->
            let (WorkAct.UpdateWork value) = request.Act

            let update name =
                function
                | WorkValue.Keep -> []
                | WorkValue.Clear -> [ name, SubmissionValue.Clear ]
                | WorkValue.Set value -> [ text name value ]

            ActKind.Work WorkActKind.UpdateWork,
            OperationBinding.Work(request.Target, request.ExpectedRevision),
            update (field BusinessField.ResponsibleActor) value.Responsible
            @ update (field BusinessField.HandlingSummary) value.Summary
        | OperationInput.Note request ->
            let selected, values =
                match request.Act with
                | AuthoredNoteAct.AddNote value -> None, [ text (input InputTarget.NoteText) value ]
                | AuthoredNoteAct.RemoveNote id -> Some id, []

            ActKind.Note(AuthoredNoteAct.kind request.Act),
            OperationBinding.Note(request.Target, selected),
            values
        | OperationInput.Reminder request ->
            let plan (value: ReminderPlanInput) =
                [
                    text (input InputTarget.ReminderAction) value.Action
                    text (input InputTarget.ReminderDueDate) value.Due
                ]

            let current, values =
                match request.Act with
                | ReminderAct.Create value -> None, plan value
                | ReminderAct.Update(id, revision, value)
                | ReminderAct.CompleteAndPlanNext(id, revision, value) ->
                    Some(id, revision), plan value
                | ReminderAct.Complete(id, revision)
                | ReminderAct.Cancel(id, revision) -> Some(id, revision), []
                | ReminderAct.Reopen(id, revision, due) ->
                    Some(id, revision), [ text (input InputTarget.ReminderDueDate) due ]

            ActKind.Reminder(AuthoredReminderAct.kind request.Act),
            OperationBinding.Reminder(request.Target, current),
            values
        | OperationInput.People request ->
            let creation, values =
                match request.Act with
                | PeopleAct.Register value -> true, profile value
                | PeopleAct.Admit value ->
                    true,
                    profile value.Profile
                    @ [
                        input InputTarget.Grants,
                        SubmissionValue.Rows(value.Grants |> List.map (grant >> Map.ofList))
                    ]
                | PeopleAct.SetProfile value -> false, profile value
                | PeopleAct.Grant value
                | PeopleAct.Revoke value -> false, grant value
                | PeopleAct.Enable
                | PeopleAct.Disable
                | PeopleAct.EraseProfile -> false, []
                | PeopleAct.ProvisionOwner _ -> invalidOp "Provisioning has no public submission."

            let values =
                if creation then
                    [
                        text
                            (input InputTarget.PrincipalKind)
                            (Vocabulary.token PrincipalKind.vocabulary request.Target.Kind)
                        text (input InputTarget.PrincipalIssuer) request.Target.Issuer
                        text (input InputTarget.PrincipalSubject) request.Target.Subject
                    ]
                    @ values
                else
                    values

            let actor =
                if creation then
                    None
                else
                    Some(
                        actorId
                        |> Option.defaultWith (fun () ->
                            invalidOp "An existing actor fixture requires its actor ID.")
                    )

            ActKind.People(PeopleAct.kind request.Act), OperationBinding.People actor, values

    OperationSubmission.admit
        {
            OperationId = OperationInput.operationId command
            Task = task
            Binding = binding
            Inputs = Map.ofList values
        }
    |> Result.defaultWith (fun () ->
        invalidOp "The transport fixture requires declared submission inputs.")

/// Typed domain assertions inspect the command assembled by the actual submission boundary.
let command (submission: OperationSubmission) =
    match submission.Operation with
    | Ok(SubmittedOperation.Command value) -> value
    | other -> failwithf "Expected an assembled command, observed %A" other
