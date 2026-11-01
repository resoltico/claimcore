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
            yield
                (RequestSchemas.PrincipalInput, WireMembers.Kind),
                constant (target InputTarget.PrincipalKind)
            yield
                (RequestSchemas.PrincipalInput, WireMembers.Issuer),
                constant (target InputTarget.PrincipalIssuer)
            yield
                (RequestSchemas.PrincipalInput, WireMembers.Subject),
                constant (target InputTarget.PrincipalSubject)
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
            for owner, value in
                [
                    "ResponsibleActorChange", BusinessField.ResponsibleActor
                    "FollowUpChange", BusinessField.FollowUpDate
                ] do
                yield (owner, "kind"), constant (annotation (InteractionInput.WorkChoice value))
                let set = Bindings.branchDefinition definitions owner "SET"
                yield (set, "value"), constant (field value)
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
            for kind in [ AuthorityKind.Grant; AuthorityKind.Revoke ] do
                yield (authorityBranch kind, WireMembers.Role), constant (target InputTarget.Role)
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
            for owner in
                [
                    RequestSchemas.PageRequest
                    RequestSchemas.AssigneeRequest
                    RequestSchemas.CaseListRequest
                    RequestSchemas.HistoryRequest
                    RequestSchemas.GrantsRequest
                    RequestSchemas.AuthorityHistoryRequest
                ] do
                yield (owner, WireMembers.Cursor), constant (target InputTarget.Cursor)
                yield (owner, WireMembers.Limit), limit
            for owner in [ RequestSchemas.CaseListRequest; RequestSchemas.CaseSummaryRequest ] do
                for name, input in
                    [
                        "lifecycle", InteractionInput.Lifecycle
                        "status", InteractionInput.Status
                        "progress", InteractionInput.Progress
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
