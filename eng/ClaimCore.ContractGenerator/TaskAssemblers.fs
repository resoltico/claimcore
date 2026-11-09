namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Emit literal typed assembly from semantic inputs, never from transport schema traversal.
module TaskAssemblers =
    let private declarationSource () =
        let input (value: TaskInputDeclaration) =
            {|
                id = TaskInputId.token value.Input
                mode = TaskInputMode.token value.Mode
                source = InputSource.token value.Source
                choices =
                    TaskChoiceId.ofInput value.Input
                    |> Option.map TaskChoiceId.tokens
                    |> Option.defaultValue []
            |}

        TaskDeclarations.all
        |> List.map (fun task ->
            {|
                id = TaskId.key task.Task
                inputs = task.Inputs |> List.map input
                collections =
                    task.Collections
                    |> List.map (fun collection ->
                        {|
                            id = TaskCollectionId.key collection.Collection
                            maximumRows = TaskCollectionId.maximumRows collection.Collection
                            inputs = collection.Inputs |> List.map input
                        |})
            |})
        |> JsonSerializer.Serialize

    let source () =
        let required =
            TaskAssembly.registrationRequired |> List.map (fst >> BusinessField.key)

        let optional =
            TaskAssembly.registrationOptional |> List.map (fst >> BusinessField.key)

        let claimedAmount, claimedCurrency = TaskMoneyId.fields TaskMoneyId.Claimed
        let paidAmount, paidCurrency = TaskMoneyId.fields TaskMoneyId.Paid

        [
            "export const taskDefinitions = " + declarationSource () + " as const;"
            "export type TaskId = (typeof taskDefinitions)[number]['id'];"
            "export const taskInputIds = "
            + JsonSerializer.Serialize(TaskInputId.all |> List.map TaskInputId.token)
            + " as const;"
            "export type TaskUpdate<T> = { readonly kind: 'KEEP' } | { readonly kind: 'SET'; readonly value: T };"
            "export type OptionalTaskUpdate<T> = TaskUpdate<T> | { readonly kind: 'CLEAR' };"
            "const selectedTaskInput = <T, U>(input: TaskUpdate<T>, value: (input: T) => U): U | Readonly<Record<string, never>> => input.kind === 'SET' ? value(input.value) : {};"
            "const optionalTaskInput = <T, U>(input: OptionalTaskUpdate<T>, value: (input: T | null) => U): U | Readonly<Record<string, never>> => input.kind === 'KEEP' ? {} : value(input.kind === 'CLEAR' ? null : input.value);"
            "export type TaskMoneyInput = { readonly amount: string; readonly currency: string };"
            "export type CaseTaskBasis = Pick<OperationInput_CASE, 'operationId' | 'target' | 'expectedRevision'>;"
            "export type RegistrationAmendmentDraft = {"
            yield! required |> List.map (fun name -> "readonly " + name + ": TaskUpdate<string>;")
            yield!
                optional
                |> List.map (fun name -> "readonly " + name + ": OptionalTaskUpdate<string>;")
            "readonly claimed: TaskUpdate<TaskMoneyInput>; };"
            "export const emptyRegistrationAmendmentDraft = (): RegistrationAmendmentDraft => ({"
            yield!
                (required @ optional @ [ "claimed" ])
                |> List.map (fun name -> name + ": { kind: 'KEEP' },")
            "});"
            "export const assembleRegistrationValues = (draft: RegistrationAmendmentDraft): RegistrationUpdate => ({"
            yield!
                required
                |> List.map (fun name ->
                    "...selectedTaskInput(draft." + name + ", value => ({ " + name + ": value })),")
            yield!
                optional
                |> List.map (fun name ->
                    "...optionalTaskInput(draft." + name + ", value => ({ " + name + ": value })),")
            "...selectedTaskInput(draft.claimed, value => ({ "
            + BusinessField.key claimedAmount
            + ": value.amount, "
            + BusinessField.key claimedCurrency
            + ": value.currency })),"
            "});"
            "export const assembleRegistrationAmendment = (basis: CaseTaskBasis, draft: RegistrationAmendmentDraft): OperationInput_CASE => ({"
            "kind: 'CASE', operationId: basis.operationId, target: basis.target, expectedRevision: basis.expectedRevision, act: { kind: 'AMEND_REGISTRATION', values: assembleRegistrationValues(draft) } });"
            "export type PaymentTaskDraft = { readonly paymentDate: string; readonly paid: TaskMoneyInput };"
            "export const assembleRecordPayment = (basis: CaseTaskBasis, draft: PaymentTaskDraft): OperationInput_CASE => ({"
            "kind: 'CASE', operationId: basis.operationId, target: basis.target, expectedRevision: basis.expectedRevision, act: { kind: 'RECORD_PAYMENT', values: {"
            BusinessField.key BusinessField.PaymentDate
            + ": draft.paymentDate, "
            + BusinessField.key paidAmount
            + ": draft.paid.amount, "
            + BusinessField.key paidCurrency
            + ": draft.paid.currency } } });"
            "export type DecisionCorrectionDraft = { readonly paymentDecisionDate: TaskUpdate<string>; readonly payable: TaskUpdate<TaskMoneyInput> };"
            "export type CorrectionTaskDraft = { readonly registration: { readonly kind: 'KEEP' } | { readonly kind: 'UPDATE'; readonly draft: RegistrationAmendmentDraft }; readonly decision: { readonly kind: 'KEEP' } | { readonly kind: 'CLEAR' } | { readonly kind: 'UPDATE'; readonly draft: DecisionCorrectionDraft } };"
            "export const assembleDecisionValues = (draft: DecisionCorrectionDraft): DecisionUpdate => ({ ...selectedTaskInput(draft.paymentDecisionDate, value => ({ paymentDecisionDate: value })), ...selectedTaskInput(draft.payable, value => ({ payableAmount: value.amount, payableCurrency: value.currency })) });"
            "const assembleCorrectionDecision = (input: CorrectionTaskDraft['decision']): DecisionGroup => { switch (input.kind) { case 'KEEP': return { kind: 'KEEP' }; case 'CLEAR': return { kind: 'CLEAR' }; case 'UPDATE': return { kind: 'UPDATE', values: assembleDecisionValues(input.draft) }; } };"
            "export const assembleCorrection = (basis: CaseTaskBasis, draft: CorrectionTaskDraft): OperationInput_CASE => ({ kind: 'CASE', operationId: basis.operationId, target: basis.target, expectedRevision: basis.expectedRevision, act: { kind: 'CORRECT', groups: { registration: draft.registration.kind === 'KEEP' ? { kind: 'KEEP' } : { kind: 'UPDATE', values: assembleRegistrationValues(draft.registration.draft) }, decision: assembleCorrectionDecision(draft.decision) } } });"
            "export type AuthorityAdmissionRow = { readonly key: string; readonly grant: AuthorityGrantInput };"
            "const taskScope = (scope: Scope): Scope => scope.kind === 'INSTALLATION' ? { kind: 'INSTALLATION' } : { kind: 'CASE', target: scope.target.kind === 'ID' ? { kind: 'ID', caseId: scope.target.caseId } : { kind: 'REFERENCE', caseReference: scope.target.caseReference } };"
            "export const assembleAuthorityAdmission = (operationId: Uuid, target: PrincipalInput, profile: ActorProfileInput, rows: ReadonlyArray<AuthorityAdmissionRow>) => ({"
            "command: { kind: 'AUTHORITY', operationId, target: { kind: target.kind, issuer: target.issuer, subject: target.subject }, act: { kind: 'ADMIT', profile: { displayName: profile.displayName, ...(profile.contact === undefined ? {} : { contact: profile.contact }) }, grants: rows.map(row => ({ role: row.grant.role, scope: taskScope(row.grant.scope) })) } } satisfies OperationInput_AUTHORITY,"
            "submittedRows: { authorityGrants: Object.freeze(rows.map(row => row.key)) } });"
        ]
        |> String.concat "\n"
