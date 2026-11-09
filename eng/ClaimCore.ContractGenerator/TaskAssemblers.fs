namespace ClaimCore.ContractGeneration

open System.Text.Json
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire

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
            let kind, subject, act, endpoint, label, consequence =
                match task.Task with
                | TaskId.Operation act ->
                    "OPERATION",
                    Some(ActSubject.token (ActKind.subject act)),
                    Some(ActKind.token act),
                    ActorEndpoint.token (ActorEndpoint.Operation OperationEndpoint.Execute),
                    Vocabulary.messageKey ActKind.vocabulary act "label",
                    Some(Vocabulary.token Consequence.vocabulary (Consequence.ofAct act))
                | TaskId.Query query ->
                    let endpoint = ActorEndpoint.Query query

                    "QUERY",
                    None,
                    None,
                    ActorEndpoint.token endpoint,
                    Vocabulary.messageKey ActorEndpoint.vocabulary endpoint "label",
                    None

            let wireAct =
                match task.Task with
                | TaskId.Operation(ActKind.Case kind) -> Some(CaseActKind.token kind)
                | TaskId.Operation(ActKind.Authority kind) -> Some(AuthorityKind.token kind)
                | TaskId.Operation(ActKind.Work kind) ->
                    Some(Vocabulary.token WorkActKind.vocabulary kind)
                | TaskId.Operation(ActKind.Note kind) -> Some(NoteActKind.token kind)
                | TaskId.Operation(ActKind.Reminder kind) -> Some(ReminderActKind.token kind)
                | TaskId.Query _ -> None

            {|
                id = TaskId.key task.Task
                wireAct = wireAct
                kind = kind
                subject = subject
                act = act
                endpoint = endpoint
                labelId = label
                consequence = consequence
                inputs = task.Inputs |> List.map input
                collections =
                    task.Collections
                    |> List.map (fun collection ->
                        {|
                            id = TaskCollectionId.key collection.Collection
                            minimumRows = TaskCollectionId.minimumRows collection.Collection
                            maximumRows = TaskCollectionId.maximumRows collection.Collection
                            inputs = collection.Inputs |> List.map input
                        |})
            |})
        |> JsonSerializer.Serialize

    let private inputPresentationSource () =
        TaskInputId.all
        |> List.map (fun input ->
            let kind, group =
                match input with
                | TaskInputId.Target(InputTarget.Field field) ->
                    "FIELD", (BusinessField.describe field).Group
                | TaskInputId.Target(InputTarget.Group group) -> "GROUP", Some group
                | _ -> "INPUT", None

            {|
                id = TaskInputId.token input
                key = TaskInputId.key input
                kind = kind
                group = group |> Option.map RecordGroup.token
                labelId = TaskInputPresentation.messageKey input "label"
                hintId = TaskInputPresentation.hintId input
                ruleId = TaskInputPresentation.messageKey input "rule"
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
            "export type TaskDefinition = (typeof taskDefinitions)[number];"
            "export type OperationTaskDefinition = Extract<TaskDefinition,{readonly kind:'OPERATION'}>;"
            "export type OperationTaskId = OperationTaskDefinition['id'];"
            "export const taskInputDefinitions = "
            + inputPresentationSource ()
            + " as const;"
            "export type TaskInputDefinition = (typeof taskInputDefinitions)[number];"
            "export const taskChoices = "
            + (TaskChoiceId.all
               |> List.map (fun choice ->
                   Vocabulary.token TaskChoiceId.vocabulary choice, TaskChoiceId.tokens choice)
               |> Map.ofList
               |> JsonSerializer.Serialize)
            + " as const;"



            "export const taskInputIds = "
            + JsonSerializer.Serialize(TaskInputId.all |> List.map TaskInputId.token)
            + " as const;"
            "export type TaskUpdate<T> = { readonly kind: 'KEEP' } | { readonly kind: 'SET'; readonly value: T };"
            "export type OptionalTaskUpdate<T> = TaskUpdate<T> | { readonly kind: 'CLEAR' };"
            "const selectedTaskInput = <T, U>(input: TaskUpdate<T>, value: (input: T) => U): U | Readonly<Record<string, never>> => input.kind === 'SET' ? value(input.value) : {};"
            "const optionalTaskInput = <T, U>(input: OptionalTaskUpdate<T>, value: (input: T | null) => U): U | Readonly<Record<string, never>> => input.kind === 'KEEP' ? {} : value(input.kind === 'CLEAR' ? null : input.value);"
            "export type TaskMoneyInput = { readonly amount: string; readonly currency: string };"
            "export type CaseTaskBasis = Pick<OperationInput_CASE, 'operationId' | 'target' | 'expectedRevision'>;"
            "export const assembleOpenCase = (operationId:Uuid,caseReference:string,values:Registration):OperationInput_CASE => ({kind:'CASE',operationId,target:{kind:'REFERENCE',caseReference},expectedRevision:0,act:{kind:'OPEN',values}});"
            "export const assembleCaseOperation = (basis:CaseTaskBasis,act:Exclude<OperationInput_CASE['act'],{readonly kind:'OPEN'|'AMEND_REGISTRATION'|'CORRECT'|'RECORD_PAYMENT'}>):OperationInput_CASE => ({kind:'CASE',operationId:basis.operationId,target:basis.target,expectedRevision:basis.expectedRevision,act});"
            "export type WorkTaskBasis = {readonly operationId:Uuid;readonly target:CaseSelector;readonly workRevision:number};"
            "export const assembleWorkOperation = (basis:WorkTaskBasis,act:OperationInput_WORK['act']):OperationInput_WORK => ({kind:'WORK',operationId:basis.operationId,target:basis.target,expectedRevision:basis.workRevision,act});"
            "export type CaseResourceTaskBasis = Pick<OperationInput_NOTE,'operationId'|'target'>;"
            "export const assembleNoteOperation = (basis:CaseResourceTaskBasis,act:OperationInput_NOTE['act']):OperationInput_NOTE => ({kind:'NOTE',operationId:basis.operationId,target:basis.target,act});"
            "export const assembleReminderOperation = (basis:CaseResourceTaskBasis,act:OperationInput_REMINDER['act']):OperationInput_REMINDER => ({kind:'REMINDER',operationId:basis.operationId,target:basis.target,act});"
            "export const assembleAuthorityOperation = (operationId:Uuid,target:PrincipalInput,act:Exclude<OperationInput_AUTHORITY['act'],{readonly kind:'ADMIT'}>):OperationInput_AUTHORITY => ({kind:'AUTHORITY',operationId,target,act});"

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
