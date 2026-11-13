namespace ClaimCore.Cli

open System
open System.Collections.Generic
open System.Globalization
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json

/// Argv acquisition is keyed by semantic input identity. Typed constructors own command structure.
module internal TaskArguments =
    let private flag input =
        "--" + CommandGrammar.kebab (CommandGrammar.inputName input)

    let private target input = TaskInputId.Target input
    let private field input = target (InputTarget.Field input)

    let mutation
        incomplete
        allocate
        kind
        (options: Dictionary<string, string option list>)
        operands
        (problems: ResizeArray<UsageError>)
        =
        let used = HashSet<string>()

        let raw input =
            let name = flag input
            used.Add name |> ignore

            match options.TryGetValue name with
            | true, values -> values |> List.tryHead |> Option.flatten
            | _ -> None

        let clear input =
            options.ContainsKey("--clear-" + CommandGrammar.kebab (CommandGrammar.inputName input))

        let required input =
            match raw input with
            | Some value -> value
            | None ->
                problems.Add(UsageError.MissingOption(flag input))
                ""

        let integer input (value: string) =
            match
                Int64.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            with
            | true, value when Revision.create value |> Option.isSome -> Some value
            | _ ->
                problems.Add(UsageError.InvalidValue(flag input, value))
                None

        let uuid input (value: string) =
            match Guid.TryParseExact(value, "D") with
            | true, value -> Some value
            | _ ->
                problems.Add(UsageError.InvalidChoice(flag input, value, []))
                None

        let choice input vocabulary =
            let value = required input

            Vocabulary.all vocabulary
            |> List.tryFind (fun item ->
                let token = Vocabulary.token vocabulary item in
                value = token || value = CommandGrammar.kebab token)
            |> Option.defaultWith (fun () ->
                problems.Add(
                    UsageError.InvalidChoice(
                        flag input,
                        value,
                        Vocabulary.all vocabulary
                        |> List.map (Vocabulary.token vocabulary >> CommandGrammar.kebab)
                    )
                )

                Vocabulary.all vocabulary |> List.head)

        let structural create values =
            create values
            |> Result.defaultWith (fun error ->
                invalidOp ("Task field declaration mismatch: " + string error))

        let group create group =
            BusinessField.ofGroup group
            |> List.choose (fun name ->
                let input = field name
                let descriptor = BusinessField.describe name

                let value =
                    if descriptor.AllowsAbsence then
                        raw input
                    else
                        Some(required input)

                value |> Option.map (fun value -> name, value))
            |> structural create

        let selected input =
            raw input |> Option.map TaskUpdate.Set |> Option.defaultValue TaskUpdate.Keep

        let optionalUpdate input =
            match raw input, clear input with
            | Some _, true ->
                problems.Add(UsageError.DuplicateOption(flag input))
                OptionalTaskUpdate.Keep
            | _, true -> OptionalTaskUpdate.Clear
            | Some value, false -> OptionalTaskUpdate.Set value
            | None, false -> OptionalTaskUpdate.Keep

        let money amount currency =
            match raw (field amount), raw (field currency) with
            | None, None -> TaskUpdate.Keep
            | amountValue, currencyValue ->
                // Both coordinates are required for a selected money input.
                let amountValue =
                    amountValue |> Option.defaultWith (fun () -> required (field amount))

                let currencyValue =
                    currencyValue |> Option.defaultWith (fun () -> required (field currency))

                TaskUpdate.Set
                    {
                        MoneyInput.Amount = amountValue
                        Currency = currencyValue
                    }

        let registrationUpdate () =
            let draft: RegistrationAmendmentDraft =
                {
                    IncidentDate = selected (field BusinessField.IncidentDate)
                    IncidentNotificationDate =
                        selected (field BusinessField.IncidentNotificationDate)
                    IncidentCountry = selected (field BusinessField.IncidentCountry)
                    IncidentDescription = optionalUpdate (field BusinessField.IncidentDescription)
                    PolicyNumber = optionalUpdate (field BusinessField.PolicyNumber)
                    InsurerClaimReference =
                        optionalUpdate (field BusinessField.InsurerClaimReference)
                    ClaimantName = selected (field BusinessField.ClaimantName)
                    InsurerName = selected (field BusinessField.InsurerName)
                    Claimed = money BusinessField.ClaimedAmount BusinessField.ClaimedCurrency
                }

            TaskAssembly.registrationValues draft |> Result.defaultWith invalidOp

        let decisionUpdate () =
            TaskAssembly.decisionValues
                {
                    PaymentDecisionDate = selected (field BusinessField.PaymentDecisionDate)
                    Payable = money BusinessField.PayableAmount BusinessField.PayableCurrency
                }
            |> Result.defaultWith invalidOp

        let profile () : ActorProfileInput =
            let input = target InputTarget.ProfileContact
            let contact = raw input

            if clear input && contact.IsSome then
                problems.Add(UsageError.DuplicateOption(flag input))

            {
                DisplayName = required (target InputTarget.ProfileDisplayName)
                Contact = if clear input then None else contact
            }

        let principal () : PrincipalInput =
            {
                Kind = choice (target InputTarget.PrincipalKind) PrincipalKind.vocabulary
                Issuer = required (target InputTarget.PrincipalIssuer)
                Subject = required (target InputTarget.PrincipalSubject)
            }

        let grant role scope : AuthoredGrant = { Role = role; Scope = scope }

        let singleGrant () =
            let role = choice (target InputTarget.Role) Role.vocabulary

            let scope =
                raw (field BusinessField.CaseReference)
                |> Option.map GrantTarget.CaseReference
                |> Option.defaultValue GrantTarget.Installation

            grant role scope

        let grantRows () =
            let name = "--role"
            used.Add name |> ignore

            let values =
                match options.TryGetValue name with
                | true, values -> values |> List.rev |> List.choose id
                | _ -> []

            if values.Length < PeopleAdmission.MinimumGrants then
                problems.Add(UsageError.MissingOption name)

            if values.Length > PeopleAdmission.MaximumGrants then
                problems.Add(
                    UsageError.InvalidChoice(name, values[PeopleAdmission.MaximumGrants], [])
                )

            values
            |> List.truncate PeopleAdmission.MaximumGrants
            |> List.map (fun value ->
                let separator = value.IndexOf('@')

                let roleText, scope =
                    if separator < 0 then
                        value, GrantTarget.Installation
                    else
                        value.Substring(0, separator),
                        GrantTarget.CaseReference(value.Substring(separator + 1))

                let role =
                    Role.all
                    |> List.tryFind (fun item ->
                        roleText = Role.token item
                        || roleText = CommandGrammar.kebab (Role.token item))
                    |> Option.defaultWith (fun () ->
                        problems.Add(
                            UsageError.InvalidChoice(
                                name,
                                value,
                                Role.all |> List.map (Role.token >> CommandGrammar.kebab)
                            )
                        )

                        List.head Role.all)

                grant role scope)

        let operation =
            match raw (target InputTarget.OperationId) with
            | Some value -> uuid (target InputTarget.OperationId) value
            | None when incomplete -> None
            | None -> uuid (target InputTarget.OperationId) (allocate ())

        let reference =
            match kind with
            | ActKind.People _ -> None
            | _ ->
                match operands with
                | value :: _ -> Some value
                | [] ->
                    problems.Add(UsageError.MissingOperand "CASE-REFERENCE")
                    None

        let revision input initial =
            match raw (target input) with
            | Some value ->
                let parsed = integer (target input) value

                if input = InputTarget.ExpectedReminderRevision && parsed = Some 0L then
                    problems.Add(UsageError.InvalidValue(flag (target input), value))
                    None
                else
                    parsed
            | None when initial -> Some 0L
            | None when incomplete -> None
            | None ->
                problems.Add(UsageError.MissingOption(flag (target input)))
                None

        let reminderId () =
            required (target InputTarget.ReminderId)
            |> uuid (target InputTarget.ReminderId)
            |> Option.defaultValue Guid.Empty

        let plan () : ReminderPlanInput =
            {
                Action = required (target InputTarget.ReminderAction)
                Due = required (target InputTarget.ReminderDueDate)
            }

        let intent =
            match kind with
            | ActKind.Case kind ->
                let expected =
                    revision
                        InputTarget.ExpectedRevision
                        ((CaseActKind.describe kind).Revision = RevisionInput.New)

                let act: AuthoredCaseAct =
                    match kind with
                    | CaseActKind.Open ->
                        group RegistrationInput.create RecordGroup.Registration |> CaseAct.Open
                    | CaseActKind.AmendRegistration ->
                        registrationUpdate () |> CaseAct.AmendRegistration
                    | CaseActKind.Decide ->
                        group DecisionInput.create RecordGroup.Decision |> CaseAct.Decide
                    | CaseActKind.RecordPayment ->
                        TaskAssembly.paymentValues
                            {
                                Date = required (field BusinessField.PaymentDate)
                                Paid =
                                    {
                                        Amount = required (field BusinessField.PaidAmount)
                                        Currency = required (field BusinessField.PaidCurrency)
                                    }
                            }
                        |> Result.defaultWith invalidOp
                        |> CaseAct.RecordPayment
                    | CaseActKind.RemovePayment ->
                        required (target InputTarget.PaymentId)
                        |> uuid (target InputTarget.PaymentId)
                        |> Option.defaultValue Guid.Empty
                        |> CaseAct.RemovePayment
                    | CaseActKind.Void -> required (target InputTarget.Reason) |> CaseAct.Void
                    | CaseActKind.Reinstate ->
                        required (target InputTarget.Reason) |> CaseAct.Reinstate
                    | CaseActKind.WithdrawDecision -> CaseAct.Bare BareAct.WithdrawDecision
                    | CaseActKind.Close -> CaseAct.Bare BareAct.Close
                    | CaseActKind.Reopen -> CaseAct.Bare BareAct.Reopen
                    | CaseActKind.Erase -> CaseAct.Bare BareAct.Erase
                    | CaseActKind.Correct ->
                        let selection group allowed =
                            let input = target (InputTarget.Group group)

                            match raw input with
                            | None -> CorrectionGroupAction.Keep
                            | Some value ->
                                allowed
                                |> List.tryFind (fun item ->
                                    value = CorrectionGroupAction.token item
                                    || value =
                                        CommandGrammar.kebab (CorrectionGroupAction.token item))
                                |> Option.defaultWith (fun () ->
                                    problems.Add(
                                        UsageError.InvalidChoice(
                                            flag input,
                                            value,
                                            allowed
                                            |> List.map (
                                                CorrectionGroupAction.token >> CommandGrammar.kebab
                                            )
                                        )
                                    )

                                    CorrectionGroupAction.Keep)

                        let registration =
                            match selection RecordGroup.Registration Amendment.actions with
                            | CorrectionGroupAction.Update ->
                                Amendment.Update(registrationUpdate ())
                            | _ -> Amendment.Keep

                        let decision =
                            match selection RecordGroup.Decision Change.actions with
                            | CorrectionGroupAction.Update -> Change.Update(decisionUpdate ())
                            | CorrectionGroupAction.Clear -> Change.Clear
                            | _ -> Change.Keep

                        CaseAct.Correct
                            {
                                Registration = registration
                                Decision = decision
                            }

                AuthoredIntent.Case(operation, reference, expected, act)
            | ActKind.People kind ->
                let target = principal ()

                let act =
                    match kind with
                    | PeopleActKind.Register -> PeopleAct.Register(profile ())
                    | PeopleActKind.Admit ->
                        PeopleAct.Admit
                            {
                                Profile = profile ()
                                Grants = grantRows ()
                            }
                    | PeopleActKind.SetProfile -> PeopleAct.SetProfile(profile ())
                    | PeopleActKind.Grant -> PeopleAct.Grant(singleGrant ())
                    | PeopleActKind.Revoke -> PeopleAct.Revoke(singleGrant ())
                    | PeopleActKind.Enable -> PeopleAct.Enable
                    | PeopleActKind.Disable -> PeopleAct.Disable
                    | PeopleActKind.EraseProfile -> PeopleAct.EraseProfile
                    | PeopleActKind.ProvisionOwner ->
                        invalidOp "Owner provisioning is not a human task."

                AuthoredIntent.People(operation, target, act)
            | ActKind.Note kind ->
                let act =
                    match kind with
                    | NoteActKind.AddNote ->
                        AuthoredNoteAct.AddNote(required (target InputTarget.NoteText))
                    | NoteActKind.RemoveNote ->
                        AuthoredNoteAct.RemoveNote(
                            required (target InputTarget.NoteId)
                            |> uuid (target InputTarget.NoteId)
                            |> Option.defaultValue Guid.Empty
                        )

                AuthoredIntent.Note(operation, reference, act)
            | ActKind.Work WorkActKind.UpdateWork ->
                let expected = revision InputTarget.ExpectedWorkRevision false
                let summaryInput = field BusinessField.HandlingSummary

                let summary =
                    match raw summaryInput, clear summaryInput with
                    | Some _, true ->
                        problems.Add(UsageError.DuplicateOption(flag summaryInput))
                        WorkValue.Keep
                    | _, true -> WorkValue.Clear
                    | Some value, false -> WorkValue.Set value
                    | None, false -> WorkValue.Keep

                let actorInput = field BusinessField.ResponsibleActor
                let actor = raw actorInput
                let actionInput = TaskInputId.ResponsibilityChange

                let responsible =
                    match raw actionInput with
                    | None ->
                        if actor.IsSome then
                            problems.Add(UsageError.NotApplicable(flag actorInput, "keep"))

                        WorkValue.Keep
                    | Some "CLEAR"
                    | Some "clear" ->
                        if actor.IsSome then
                            problems.Add(UsageError.NotApplicable(flag actorInput, "clear"))

                        WorkValue.Clear
                    | Some "SET"
                    | Some "set" -> WorkValue.Set(required actorInput)
                    | Some value ->
                        problems.Add(
                            UsageError.InvalidChoice(
                                flag actionInput,
                                value,
                                TaskChoiceId.tokens TaskChoiceId.WorkChange
                                |> List.map CommandGrammar.kebab
                            )
                        )

                        WorkValue.Keep

                AuthoredIntent.Work(
                    operation,
                    reference,
                    expected,
                    {
                        Responsible = responsible
                        Summary = summary
                    }
                )
            | ActKind.Reminder kind ->
                let act =
                    match kind with
                    | ReminderActKind.Create -> ReminderAct.Create(plan ())
                    | _ ->
                        let id = reminderId ()
                        let expected = revision InputTarget.ExpectedReminderRevision false

                        match kind with
                        | ReminderActKind.Update -> ReminderAct.Update(id, expected, plan ())
                        | ReminderActKind.Complete -> ReminderAct.Complete(id, expected)
                        | ReminderActKind.Cancel -> ReminderAct.Cancel(id, expected)
                        | ReminderActKind.Reopen ->
                            ReminderAct.Reopen(
                                id,
                                expected,
                                required (target InputTarget.ReminderDueDate)
                            )
                        | ReminderActKind.CompleteAndPlanNext ->
                            ReminderAct.CompleteAndPlanNext(id, expected, plan ())
                        | ReminderActKind.Create -> invalidOp "Creation handled above."

                AuthoredIntent.Reminder(operation, reference, act)

        let expectedOperands = if reference.IsSome then 1 else 0

        operands
        |> List.skip (min operands.Length expectedOperands)
        |> List.iter (UsageError.ExtraOperand >> problems.Add)

        for name in options.Keys do
            if
                not (
                    used.Contains name
                    || List.contains name CommandGrammar.globalFlags
                    || (name.StartsWith("--clear-", StringComparison.Ordinal)
                        && used.Contains("--" + name.Substring(8)))
                )
            then
                problems.Add(UsageError.NotApplicable(name, ActKind.token kind))

        intent

    /// Draft encodings omit unknown metadata; executable admission remains a separate boundary.
    let write intent =
        let request subject targetCodec actCodec target act operation revision =
            (Codec.record (fun target act operation revision -> target, act, operation, revision)
             |> Codec.fixedField "kind" (ActSubject.token subject)
             |> Codec.field "target" (fun (target, _, _, _) -> target) targetCodec
             |> Codec.field "act" (fun (_, act, _, _) -> act) actCodec
             |> Codec.optionalField "operationId" (fun (_, _, operation, _) -> operation) Codec.uuid
             |> Codec.optionalField
                 "expectedRevision"
                 (fun (_, _, _, revision) -> revision)
                 RequestCodecs.revision
             |> Codec.build)
                .Write(target, act, operation, revision)

        let caseTarget reference =
            reference
            |> Option.map CaseSelector.Reference
            |> Option.defaultWith (fun () ->
                invalidOp "Missing case operand must be reported before draft encoding.")

        match intent with
        | AuthoredIntent.Case(operation, reference, revision, act) ->
            request
                ActSubject.Case
                RequestCodecs.caseSelector
                RequestCodecs.caseAct
                (caseTarget reference)
                act
                operation
                revision
        | AuthoredIntent.Work(operation, reference, revision, update) ->
            request
                ActSubject.Work
                RequestCodecs.caseSelector
                RequestCodecs.workAct
                (caseTarget reference)
                (WorkAct.UpdateWork update)
                operation
                revision
        | AuthoredIntent.Note(operation, reference, act) ->
            request
                ActSubject.Note
                RequestCodecs.caseSelector
                RequestCodecs.noteAct
                (caseTarget reference)
                act
                operation
                None
        | AuthoredIntent.People(operation, target, act) ->
            request
                ActSubject.People
                RequestCodecs.principal
                RequestCodecs.peopleAct
                target
                act
                operation
                None
        | AuthoredIntent.Reminder(operation, reference, act) ->
            let identity =
                Codec.record (fun id revision -> id, revision)
                |> Codec.field "reminderId" fst Codec.uuid
                |> Codec.optionalField "expectedRevision" snd RequestCodecs.reminderRevision

            request
                ActSubject.Reminder
                RequestCodecs.caseSelector
                (RequestCodecs.reminderActWith identity)
                (caseTarget reference)
                act
                operation
                None
