namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module CaseActAnnotations =
    let prefill =
        set [ CaseActKind.AmendRegistration; CaseActKind.Correct; CaseActKind.Decide ]

/// Meaning attaches to named codec members; structural paths and alternatives belong to the schema.
module CatalogAnnotations =
    let private annotation name =
        {
            Name = name
            Source = InputSource.Authored
            Prefill = None
            Default = None
            Implicit = None
            Fixed = None
        }

    let private field field =
        annotation (InteractionInput.Target(InputTarget.Field field))

    let private target target =
        annotation (InteractionInput.Target target)

    let private operationIdentity (operation: CatalogOperation) =
        { target InputTarget.OperationId with
            Source =
                if operation.SelectedAct.IsSome then
                    InputSource.OperationIdentity
                else
                    InputSource.Authored
        }

    let private caseReference _ =
        { field BusinessField.CaseReference with
            Prefill = Some [ BusinessField.key BusinessField.CaseReference ]
        }

    let private revision (operation: CatalogOperation) =
        let fixedValue =
            match operation.SelectedAct with
            | Some(ActKind.Case kind) when (CaseActKind.describe kind).Revision = RevisionInput.New ->
                Some(BindingValue.Integer 0L)
            | _ -> None

        { target InputTarget.ExpectedRevision with
            Source = InputSource.ExpectedRevision
            Fixed = fixedValue
        }

    let private business group value (operation: CatalogOperation) =
        let prefill =
            match operation.SelectedAct with
            | Some(ActKind.Case kind) when Set.contains kind CaseActAnnotations.prefill ->
                Some [ RecordGroup.token group; BusinessField.key value ]
            | _ -> None

        { field value with Prefill = prefill }

    let private constant value _ = value

    let private scope _ =
        { annotation InteractionInput.Scope with
            Implicit =
                Some(
                    BindingValue.Text(ScopeKind.token ScopeKind.Installation),
                    "Scope is supplied by the interaction; installation is the implicit choice."
                )
        }

    let private detail _ =
        { annotation InteractionInput.Detail with
            Implicit =
                Some(
                    BindingValue.Text(
                        Vocabulary.token HistoryDetail.vocabulary HistoryDetail.Summary
                    ),
                    "The history switch supplies SUMMARY unless FULL is requested."
                )
        }

    let private limit _ =
        { target InputTarget.Limit with
            Default = Some(BindingValue.Integer(int64 Paging.DefaultPageSize))
        }

    let create (definitions: Map<string, Schema>) =
        let caseRequest =
            Bindings.branchDefinition
                definitions
                RequestSchemas.OperationInput
                (ActSubject.token ActSubject.Case)

        let authorityRequest =
            Bindings.branchDefinition
                definitions
                RequestSchemas.OperationInput
                (ActSubject.token ActSubject.Authority)

        let workRequest =
            Bindings.branchDefinition definitions RequestSchemas.OperationInput "WORK"

        let reminderRequest =
            Bindings.branchDefinition definitions RequestSchemas.OperationInput "REMINDER"

        let actBranch kind =
            Bindings.branchDefinition definitions RequestSchemas.CaseAct (CaseActKind.token kind)

        let authorityBranch kind =
            Bindings.branchDefinition
                definitions
                RequestSchemas.AuthorityAct
                (AuthorityKind.token kind)

        [
            yield (caseRequest, WireMembers.OperationId), operationIdentity
            yield
                (Bindings.branchDefinition definitions RequestSchemas.CaseSelector "REFERENCE",
                 BusinessField.key BusinessField.CaseReference),
                caseReference
            yield (caseRequest, WireMembers.ExpectedRevision), revision
            yield (authorityRequest, WireMembers.OperationId), operationIdentity
            yield (workRequest, WireMembers.OperationId), operationIdentity
            yield (reminderRequest, WireMembers.OperationId), operationIdentity
            yield
                (workRequest, "expectedRevision"),
                (fun _ ->
                    { target InputTarget.ExpectedWorkRevision with
                        Source = InputSource.ExpectedRevision
                    })
            yield
                ("CaseWorkUpdate", "handlingSummary"),
                (fun _ ->
                    { field BusinessField.HandlingSummary with
                        Prefill = Some [ "work"; "summary" ]
                    })
            for kind in ReminderActKind.all do
                let owner =
                    Bindings.branchDefinition definitions "ReminderAct" (ReminderActKind.token kind)

                if kind <> ReminderActKind.Create then
                    yield (owner, "reminderId"), constant (target InputTarget.ReminderId)

                    yield
                        (owner, "expectedRevision"),
                        (fun _ ->
                            { target InputTarget.ExpectedReminderRevision with
                                Source = InputSource.ExpectedRevision
                            })

                if
                    List.contains
                        kind
                        [
                            ReminderActKind.Create
                            ReminderActKind.Update
                            ReminderActKind.CompleteAndPlanNext
                        ]
                then
                    yield (owner, "action"), constant (target InputTarget.ReminderAction)

                if
                    List.contains
                        kind
                        [
                            ReminderActKind.Create
                            ReminderActKind.Update
                            ReminderActKind.Reopen
                            ReminderActKind.CompleteAndPlanNext
                        ]
                then
                    yield (owner, "dueDate"), constant (target InputTarget.ReminderDueDate)

            for owner in
                [ RequestSchemas.PrincipalInput; RequestSchemas.AccessRequestDismissRequest ] do
                yield (owner, WireMembers.Kind), constant (target InputTarget.PrincipalKind)
                yield (owner, WireMembers.Issuer), constant (target InputTarget.PrincipalIssuer)
                yield (owner, WireMembers.Subject), constant (target InputTarget.PrincipalSubject)
            for owner, group in
                [
                    RequestSchemas.Registration, RecordGroup.Registration
                    RequestSchemas.Decision, RecordGroup.Decision
                    RequestSchemas.Payment, RecordGroup.Payment
                    "RegistrationUpdate", RecordGroup.Registration
                    "DecisionUpdate", RecordGroup.Decision
                ] do
                for value in CaseFields.ofGroup group do
                    if
                        (owner <> "RegistrationUpdate" && owner <> "DecisionUpdate")
                        || not (
                            List.contains
                                value
                                [
                                    BusinessField.ClaimedAmount
                                    BusinessField.ClaimedCurrency
                                    BusinessField.PayableAmount
                                    BusinessField.PayableCurrency
                                ]
                        )
                    then
                        yield (owner, BusinessField.key value), business group value
            for owner, group, amount, currency in
                [
                    "RegistrationUpdate",
                    RecordGroup.Registration,
                    BusinessField.ClaimedAmount,
                    BusinessField.ClaimedCurrency
                    "DecisionUpdate",
                    RecordGroup.Decision,
                    BusinessField.PayableAmount,
                    BusinessField.PayableCurrency
                ] do
                yield (owner, "amount"), business group amount
                yield (owner, "currency"), business group currency
            for owner, value in [ "ResponsibleActorChange", BusinessField.ResponsibleActor ] do
                yield (owner, "kind"), constant (annotation (InteractionInput.WorkChoice value))
                let set = Bindings.branchDefinition definitions owner "SET"

                yield
                    (set, "value"),
                    (fun _ ->
                        { field value with
                            Prefill = Some [ "work"; "responsible"; "account"; "actorId" ]
                        })
            for owner, group in
                [
                    RequestSchemas.RegistrationGroup, RecordGroup.Registration
                    RequestSchemas.DecisionGroup, RecordGroup.Decision
                    RequestSchemas.PaymentGroup, RecordGroup.Payment
                ] do
                yield
                    (owner, WireMembers.Kind),
                    constant (annotation (InteractionInput.Target(InputTarget.Group group)))
            for kind in [ CaseActKind.Void; CaseActKind.Reinstate ] do
                yield (actBranch kind, WireMembers.Reason), constant (target InputTarget.Reason)
            yield
                (authorityBranch AuthorityKind.Admit, "grants"),
                constant (target InputTarget.Grants)
            yield ("AuthorityGrantInput", WireMembers.Role), constant (target InputTarget.Role)
            for kind in [ AuthorityKind.Grant; AuthorityKind.Revoke ] do
                yield (authorityBranch kind, WireMembers.Role), constant (target InputTarget.Role)
            yield
                ("ActorProfileInput", "displayName"),
                constant (target InputTarget.ProfileDisplayName)
            yield ("ActorProfileInput", "contact"), constant (target InputTarget.ProfileContact)
            yield (RequestSchemas.Scope, WireMembers.Kind), scope
            for owner in
                [
                    RequestSchemas.ObserveRequest
                    RequestSchemas.ResolveRequest
                    RequestSchemas.StatusRequest
                ] do
                yield (owner, WireMembers.OperationId), operationIdentity
            for owner in [ RequestSchemas.GrantsRequest; RequestSchemas.AuthorityHistoryRequest ] do
                yield (owner, WireMembers.ActorId), constant (annotation InteractionInput.ActorId)
            yield (RequestSchemas.HistoryRequest, WireMembers.Detail), detail
            yield ("CaseActivityRequest", WireMembers.Detail), detail
            yield
                ("ReminderListRequest", "state"),
                constant (annotation InteractionInput.ReminderState)
            yield ("ReminderListRequest", "due"), constant (annotation InteractionInput.ReminderDue)
            yield
                ("ReminderListRequest", "assignment"),
                constant (annotation InteractionInput.Assignment)
            yield
                ("ReminderListRequest", "view"), constant (annotation InteractionInput.ReminderView)
            yield ("ReminderListRequest", "text"), constant (target InputTarget.Text)
            yield
                ("ReminderListRequest", "includeInactive"),
                constant (annotation InteractionInput.IncludeInactive)
            yield
                (RequestSchemas.ActorListRequest, "text"),
                constant (target InputTarget.DirectorySearch)
            yield
                (RequestSchemas.ActorListRequest, "role"),
                constant (annotation InteractionInput.RoleFilter)
            yield ("WorkOverviewRequest", "mine"), constant (annotation InteractionInput.Mine)
            yield ("ReminderGetRequest", "reminderId"), constant (target InputTarget.ReminderId)
            for owner in
                [
                    RequestSchemas.ActorListRequest
                    RequestSchemas.AccessRequestsRequest
                    RequestSchemas.AssigneeRequest
                    RequestSchemas.CaseListRequest
                    RequestSchemas.HistoryRequest
                    "CaseActivityRequest"
                    "CaseTimelineRequest"
                    "ReminderListRequest"
                    RequestSchemas.GrantsRequest
                    RequestSchemas.AuthorityHistoryRequest
                ] do
                yield (owner, WireMembers.Cursor), constant (target InputTarget.Cursor)
                yield (owner, WireMembers.Limit), limit
            yield
                ("CaseTimelineRequest", "filter"),
                constant (annotation InteractionInput.TimelineFilter)
            for owner in [ RequestSchemas.CaseListRequest; RequestSchemas.CaseSummaryRequest ] do
                for name, input in
                    [
                        "lifecycle", InteractionInput.Lifecycle
                        "status", InteractionInput.Status
                        "progress", InteractionInput.Progress
                        "view", InteractionInput.CaseView
                        "responsibleActor", InteractionInput.ResponsibleActorFilter
                        "text", InteractionInput.Target InputTarget.Text
                    ] do
                    yield (owner, name), constant (annotation input)

                yield (owner, "assignment"), constant (annotation InteractionInput.Assignment)
                yield (owner, "due"), constant (annotation InteractionInput.Due)

                yield
                    (owner, "order"),
                    constant
                        { annotation InteractionInput.Order with
                            Default =
                                Some(BindingValue.Text(CaseListOrder.token CaseListOrder.Reference))
                        }
        ]
        |> Map.ofList
