namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

/// Executable assembly always requires a real operation identity and reviewed basis.
type CaseTaskBasis =
    {
        Operation: OperationId
        Target: CaseSelector
        Revision: Revision
    }

type RegistrationAmendmentDraft =
    {
        IncidentDate: TaskUpdate<string>
        IncidentNotificationDate: TaskUpdate<string>
        IncidentCountry: TaskUpdate<string>
        IncidentDescription: OptionalTaskUpdate<string>
        PolicyNumber: OptionalTaskUpdate<string>
        InsurerClaimReference: OptionalTaskUpdate<string>
        ClaimantName: TaskUpdate<string>
        InsurerName: TaskUpdate<string>
        Claimed: TaskUpdate<MoneyInput>
    }

module RegistrationAmendmentDraft =
    let empty =
        {
            IncidentDate = TaskUpdate.Keep
            IncidentNotificationDate = TaskUpdate.Keep
            IncidentCountry = TaskUpdate.Keep
            IncidentDescription = OptionalTaskUpdate.Keep
            PolicyNumber = OptionalTaskUpdate.Keep
            InsurerClaimReference = OptionalTaskUpdate.Keep
            ClaimantName = TaskUpdate.Keep
            InsurerName = TaskUpdate.Keep
            Claimed = TaskUpdate.Keep
        }

type PaymentTaskDraft = { Date: string; Paid: MoneyInput }

type DecisionCorrectionDraft =
    {
        PaymentDecisionDate: TaskUpdate<string>
        Payable: TaskUpdate<MoneyInput>
    }

type CorrectionTaskDraft =
    {
        Registration: Amendment<RegistrationAmendmentDraft>
        Decision: Change<DecisionCorrectionDraft>
    }

/// Row keys are local correlation metadata; only Grant becomes authored business content.
type AuthorityAdmissionRow<'key> = { Key: 'key; Grant: AuthoredGrant }

type AuthorityAdmissionDraft<'key> =
    {
        Target: PrincipalInput
        Profile: ActorProfileInput
        Grants: AuthorityAdmissionRow<'key> list
    }

type TaskSubmission<'key> =
    {
        Command: OperationInput
        SubmittedRows: Map<TaskCollectionId, 'key list>
    }

module TaskAssembly =
    let private selected field =
        function
        | TaskUpdate.Keep -> []
        | TaskUpdate.Set value -> [ field, Some value ]

    let private optional field =
        function
        | OptionalTaskUpdate.Keep -> []
        | OptionalTaskUpdate.Clear -> [ field, None ]
        | OptionalTaskUpdate.Set value -> [ field, Some value ]

    let private money input (selection: TaskUpdate<MoneyInput>) =
        match selection with
        | TaskUpdate.Keep -> []
        | TaskUpdate.Set value ->
            let amount, currency = TaskMoneyId.fields input
            [ amount, Some value.Amount; currency, Some value.Currency ]

    let registrationRequired
        : (BusinessField * (RegistrationAmendmentDraft -> TaskUpdate<string>)) list =
        [
            BusinessField.IncidentDate, _.IncidentDate
            BusinessField.IncidentNotificationDate, _.IncidentNotificationDate
            BusinessField.IncidentCountry, _.IncidentCountry
            BusinessField.ClaimantName, _.ClaimantName
            BusinessField.InsurerName, _.InsurerName
        ]

    let registrationOptional
        : (BusinessField * (RegistrationAmendmentDraft -> OptionalTaskUpdate<string>)) list =
        [
            BusinessField.IncidentDescription, _.IncidentDescription
            BusinessField.PolicyNumber, _.PolicyNumber
            BusinessField.InsurerClaimReference, _.InsurerClaimReference
        ]

    let registrationValues (draft: RegistrationAmendmentDraft) =
        [
            for field, read in registrationRequired do
                yield! selected field (read draft)
            for field, read in registrationOptional do
                yield! optional field (read draft)
            yield! money TaskMoneyId.Claimed draft.Claimed
        ]
        |> RegistrationUpdateInput.create

    let private caseCommand basis act =
        OperationInput.Case
            {
                OperationId = OperationId.value basis.Operation
                Target = basis.Target
                ExpectedRevision = Revision.value basis.Revision
                Act = act
            }

    let amendRegistration basis draft =
        registrationValues draft
        |> Result.map (CaseAct.AmendRegistration >> caseCommand basis)

    let paymentValues (draft: PaymentTaskDraft) =
        PaymentInput.create
            [
                BusinessField.PaymentDate, draft.Date
                BusinessField.PaidAmount, draft.Paid.Amount
                BusinessField.PaidCurrency, draft.Paid.Currency
            ]

    let recordPayment basis draft =
        paymentValues draft |> Result.map (CaseAct.RecordPayment >> caseCommand basis)

    let decisionValues (draft: DecisionCorrectionDraft) =
        selected BusinessField.PaymentDecisionDate draft.PaymentDecisionDate
        @ money TaskMoneyId.Payable draft.Payable
        |> DecisionUpdateInput.create

    let correct basis (draft: CorrectionTaskDraft) =
        let registration =
            match draft.Registration with
            | Amendment.Keep -> Ok Amendment.Keep
            | Amendment.Update input -> registrationValues input |> Result.map Amendment.Update

        let decision =
            match draft.Decision with
            | Change.Keep -> Ok Change.Keep
            | Change.Clear -> Ok Change.Clear
            | Change.Update input -> decisionValues input |> Result.map Change.Update

        registration
        |> Result.bind (fun registration ->
            decision
            |> Result.map (fun decision ->
                CaseAct.Correct
                    {
                        Registration = registration
                        Decision = decision
                    }
                |> caseCommand basis))

    let admitAuthority operation (draft: AuthorityAdmissionDraft<'key>) =
        {
            Command =
                OperationInput.Authority
                    {
                        OperationId = OperationId.value operation
                        Target = draft.Target
                        Act =
                            AuthorityAct.Admit
                                {
                                    Profile = draft.Profile
                                    Grants = draft.Grants |> List.map _.Grant
                                }
                    }
            SubmittedRows =
                Map.ofList [ TaskCollectionId.AuthorityGrants, draft.Grants |> List.map _.Key ]
        }

    /// Complete acquired metadata without allocating an identity or inventing a reviewed revision.
    let completeIntent intent =
        let caseTarget reference =
            reference |> Option.map CaseSelector.Reference

        let pair left right =
            Option.map2 (fun left right -> left, right) left right

        let reminder =
            function
            | ReminderAct.Create plan -> Some(ReminderAct.Create plan)
            | ReminderAct.Update(id, revision, plan) ->
                revision |> Option.map (fun revision -> ReminderAct.Update(id, revision, plan))
            | ReminderAct.Complete(id, revision) ->
                revision |> Option.map (fun revision -> ReminderAct.Complete(id, revision))
            | ReminderAct.Cancel(id, revision) ->
                revision |> Option.map (fun revision -> ReminderAct.Cancel(id, revision))
            | ReminderAct.Reopen(id, revision, due) ->
                revision |> Option.map (fun revision -> ReminderAct.Reopen(id, revision, due))
            | ReminderAct.CompleteAndPlanNext(id, revision, plan) ->
                revision
                |> Option.map (fun revision -> ReminderAct.CompleteAndPlanNext(id, revision, plan))

        match intent with
        | AuthoredIntent.Case(operation, reference, revision, act) ->
            pair (pair operation (caseTarget reference)) revision
            |> Option.map (fun ((operation, target), revision) ->
                OperationInput.Case
                    {
                        OperationId = operation
                        Target = target
                        ExpectedRevision = revision
                        Act = act
                    })
        | AuthoredIntent.Work(operation, reference, revision, update) ->
            pair (pair operation (caseTarget reference)) revision
            |> Option.map (fun ((operation, target), revision) ->
                OperationInput.Work
                    {
                        OperationId = operation
                        Target = target
                        ExpectedRevision = revision
                        Act = WorkAct.UpdateWork update
                    })
        | AuthoredIntent.Note(operation, reference, act) ->
            pair operation (caseTarget reference)
            |> Option.map (fun (operation, target) ->
                OperationInput.Note
                    {
                        OperationId = operation
                        Target = target
                        Act = act
                    })
        | AuthoredIntent.Authority(operation, target, act) ->
            operation
            |> Option.map (fun operation ->
                OperationInput.Authority
                    {
                        OperationId = operation
                        Target = target
                        Act = act
                    })
        | AuthoredIntent.Reminder(operation, reference, act) ->
            pair (pair operation (caseTarget reference)) (reminder act)
            |> Option.map (fun ((operation, target), act) ->
                OperationInput.Reminder
                    {
                        OperationId = operation
                        Target = target
                        Act = act
                    })
