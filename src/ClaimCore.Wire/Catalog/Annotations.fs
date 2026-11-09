namespace ClaimCore.Wire

open ClaimCore.Presentation

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
        annotation (TaskInputId.Target(InputTarget.Field field))

    let private target target = annotation (TaskInputId.Target target)

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
            | Some(ActKind.Case CaseActKind.RecordPayment) when value = BusinessField.PaidCurrency ->
                Some [ "decision"; "payableCurrency" ]
            | Some(ActKind.Case kind) when Set.contains kind CaseActAnnotations.prefill ->
                Some [ RecordGroup.token group; BusinessField.key value ]
            | _ -> None

        { field value with Prefill = prefill }

    let private constant value _ = value

    let private scope _ =
        { annotation TaskInputId.Scope with
            Implicit =
                Some(
                    BindingValue.Text(ScopeKind.token ScopeKind.Installation),
                    "Scope is supplied by the interaction; installation is the implicit choice."
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
                (Bindings.branchDefinition definitions RequestSchemas.OperationInput "NOTE",
                 WireMembers.OperationId),
                operationIdentity
            yield
                (Bindings.branchDefinition definitions "NoteAct" "ADD_NOTE", "text"),
                constant (target InputTarget.NoteText)
            yield
                (Bindings.branchDefinition definitions "NoteAct" "REMOVE_NOTE", "noteId"),
                constant (target InputTarget.NoteId)
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
                    yield (owner, BusinessField.key value), business group value
            for owner, value in [ "ResponsibleActorChange", BusinessField.ResponsibleActor ] do
                yield (owner, "kind"), constant (annotation (TaskInputId.ResponsibilityChange))
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
                ] do
                yield
                    (owner, WireMembers.Kind),
                    constant (annotation (TaskInputId.Target(InputTarget.Group group)))
            yield
                (actBranch CaseActKind.RemovePayment, "paymentId"),
                constant (target InputTarget.PaymentId)
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
            yield (RequestSchemas.OperationGetRequest, WireMembers.OperationId), operationIdentity
            for owner in [ RequestSchemas.GrantsRequest; RequestSchemas.AuthorityHistoryRequest ] do
                yield (owner, WireMembers.ActorId), constant (annotation TaskInputId.ActorId)
            yield ("ReminderListRequest", "state"), constant (annotation TaskInputId.ReminderState)
            yield ("ReminderListRequest", "due"), constant (annotation TaskInputId.ReminderDue)
            yield
                ("ReminderListRequest", "assignment"), constant (annotation TaskInputId.Assignment)
            yield ("ReminderListRequest", "view"), constant (annotation TaskInputId.ReminderView)
            yield ("ReminderListRequest", "text"), constant (target InputTarget.Text)
            yield
                ("ReminderListRequest", "includeInactive"),
                constant (annotation TaskInputId.IncludeInactive)
            yield
                (RequestSchemas.ActorListRequest, "text"),
                constant (target InputTarget.DirectorySearch)
            yield
                (RequestSchemas.ActorListRequest, "role"),
                constant (annotation TaskInputId.RoleFilter)
            yield ("ReminderGetRequest", "reminderId"), constant (target InputTarget.ReminderId)
            for owner in
                [
                    RequestSchemas.ActorListRequest
                    RequestSchemas.AccessRequestsRequest
                    RequestSchemas.AssigneeRequest
                    RequestSchemas.CaseListRequest
                    "CaseNotesRequest"
                    "CasePaymentsRequest"
                    "CaseTimelineRequest"
                    "ReminderListRequest"
                    RequestSchemas.GrantsRequest
                    RequestSchemas.AuthorityHistoryRequest
                ] do
                yield (owner, WireMembers.Cursor), constant (target InputTarget.Cursor)

                yield
                    (owner, WireMembers.Limit),
                    (if owner = "CaseNotesRequest" || owner = "CasePaymentsRequest" then
                         (fun _ ->
                             { target InputTarget.Limit with
                                 Default =
                                     Some(BindingValue.Integer(int64 NotePaging.DefaultLimit))
                             })
                     else
                         limit)
            yield
                ("CaseTimelineRequest", "filter"), constant (annotation TaskInputId.TimelineFilter)
            for owner in [ RequestSchemas.CaseListRequest; RequestSchemas.CaseSummaryRequest ] do
                for name, input in
                    [
                        "lifecycle", TaskInputId.Lifecycle
                        "status", TaskInputId.Status
                        "progress", TaskInputId.Progress
                        "view", TaskInputId.CaseView
                        "responsibleActor", TaskInputId.ResponsibleActorFilter
                        "text", TaskInputId.Target InputTarget.Text
                    ] do
                    yield (owner, name), constant (annotation input)

                yield (owner, "assignment"), constant (annotation TaskInputId.Assignment)
                yield (owner, "due"), constant (annotation TaskInputId.Due)

                yield
                    (owner, "order"),
                    constant
                        { annotation TaskInputId.Order with
                            Default =
                                Some(BindingValue.Text(CaseListOrder.token CaseListOrder.Reference))
                        }
        ]
        |> Map.ofList
