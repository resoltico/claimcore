namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

type ValidAuthorityAct = AuthorityAct<AuthoredGrant, ActorProfile>

type AuthorityCommand =
    private
        {
            Id: OperationId
            Principal: PrincipalKey
            Action: ValidAuthorityAct
        }

    member this.OperationId = this.Id
    member this.Target = this.Principal
    member this.Act = this.Action

[<RequireQualifiedAccess>]
type AuthorityChange =
    | Registered of ActorProfile
    | Admitted of ActorProfile * Grant<Scope> list
    | OwnerProvisioned of ActorProfile
    | Granted of Role * Scope
    | Revoked of Role * Scope
    | Enabled
    | Disabled
    | ProfileSet of ActorProfile
    | ProfileErased

[<RequireQualifiedAccess>]
type AuthorityFact =
    | ActorCount
    | EnabledHumanOwners

type AuthorityFacts =
    {
        TargetActor: ActorAuthority option
        TargetCases: Map<GrantTarget, CaseId>
        EnabledHumanOwners: int option
        ActorCount: int option
        Requester: AuthorityView
    }

/// Potential sources remain visible for disabled actors; Effective reflects current actor validity.
type CapabilityAccess =
    {
        Capability: Capability
        DirectRoles: Role list
        InstallationRoles: Role list
        Effective: bool
    }

module Authority =
    /// Only these acts need installation-wide protection facts; callers load them in the action snapshot.
    let factsNeeded (act: ValidAuthorityAct) =
        match act with
        | AuthorityAct.ProvisionOwner _ -> [ AuthorityFact.ActorCount ]
        | AuthorityAct.Disable
        | AuthorityAct.Revoke {
                                  Role = Role.Owner
                                  Scope = GrantTarget.Installation
                              } -> [ AuthorityFact.EnabledHumanOwners ]
        | _ -> []

    let private required name value =
        value
        |> Option.defaultWith (fun () -> invalidOp ("Missing authority protection fact: " + name))

    // Roles, principal restrictions and scoped capabilities have one domain owner.
    let policy =
        function
        | Role.Owner -> [ Capability.ManageAuthority; Capability.ReadAuthority ]
        | Role.CaseReader -> [ Capability.ReadCase; Capability.ReadHistory ]
        | Role.CaseRegistrar -> [ Capability.ReadCase; Capability.ReadHistory; Capability.Register ]
        | Role.CaseHandler ->
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.Register
                Capability.Decide
                Capability.Close
                Capability.ManageCaseWork
            ]
        | Role.PaymentRecorder ->
            [ Capability.ReadCase; Capability.ReadHistory; Capability.RecordPayment ]
        | Role.DataSteward -> [ Capability.ReadCase; Capability.ReadHistory; Capability.Steward ]
        | Role.Auditor ->
            [
                Capability.ReadCase
                Capability.ReadHistory
                Capability.ReadHistoryFull
                Capability.ReadAuthority
            ]

    let roles = Role.all

    let rolesFor capability =
        roles |> List.filter (fun role -> List.contains capability (policy role))

    /// Scope meaning belongs to capability policy, independently of principal-kind admission.
    let capabilityScopes =
        function
        | Capability.ManageAuthority
        | Capability.ReadAuthority -> [ ScopeKind.Installation ]
        | Capability.ReadCase
        | Capability.ReadHistory
        | Capability.ReadHistoryFull
        | Capability.Register
        | Capability.Decide
        | Capability.RecordPayment
        | Capability.Close
        | Capability.ManageCaseWork
        | Capability.Steward -> ScopeKind.all

    let roleScopes role =
        ScopeKind.all
        |> List.filter (fun scope ->
            policy role
            |> List.exists (fun capability -> List.contains scope (capabilityScopes capability)))

    /// Capability restrictions apply when exercising a right, not to every capability of its role.
    let humanOnlyCapability =
        function
        | Capability.Steward
        | Capability.ManageCaseWork
        | Capability.ManageAuthority -> true
        | Capability.ReadCase
        | Capability.ReadHistory
        | Capability.ReadHistoryFull
        | Capability.Register
        | Capability.Decide
        | Capability.RecordPayment
        | Capability.Close
        | Capability.ReadAuthority -> false

    let humanOnlyRole =
        function
        | Role.Owner
        | Role.DataSteward -> true
        | Role.CaseReader
        | Role.CaseRegistrar
        | Role.CaseHandler
        | Role.PaymentRecorder
        | Role.Auditor -> false

    let invalidRoles principal =
        if PrincipalKey.isHuman principal then
            []
        else
            roles |> List.filter humanOnlyRole

    let readRoles principal =
        rolesFor Capability.ReadCase
        |> List.filter (fun role -> PrincipalKey.isHuman principal || not (humanOnlyRole role))

    let validActor principal (view: AuthorityView) =
        let loaded =
            view.Installation @ (view.Cases |> Map.values |> Seq.collect id |> Seq.toList)

        view.Actor
        |> Option.exists (fun actor ->
            actor.Enabled
            && actor.Principal = principal
            && (PrincipalKey.isHuman principal || loaded |> List.forall (humanOnlyRole >> not)))

    let can principal (view: AuthorityView) capability resource =
        let scoped =
            match resource with
            | Scope.Installation -> Some view.Installation
            | Scope.Case id when List.contains ScopeKind.Case (capabilityScopes capability) ->
                Some(view.Installation @ (view.Cases |> Map.tryFind id |> Option.defaultValue []))
            | _ -> None

        validActor principal view
        && (not (humanOnlyCapability capability) || PrincipalKey.isHuman principal)
        && (scoped
            |> Option.exists (List.exists (fun role -> List.contains capability (policy role))))

    /// Explain only the supplied resource snapshot; this does not predict business-act eligibility.
    let explain principal (view: AuthorityView) resource =
        let kind, direct, inherited =
            match resource with
            | Scope.Installation -> ScopeKind.Installation, view.Installation, []
            | Scope.Case id ->
                ScopeKind.Case,
                (view.Cases |> Map.tryFind id |> Option.defaultValue []),
                view.Installation

        Capability.all
        |> List.choose (fun capability ->
            let sources roles =
                roles
                |> List.filter (fun role -> List.contains capability (policy role))
                |> List.distinct

            let directRoles = sources direct
            let installationRoles = sources inherited

            if
                not (List.contains kind (capabilityScopes capability))
                || (directRoles.IsEmpty && installationRoles.IsEmpty)
            then
                None
            else
                Some
                    {
                        Capability = capability
                        DirectRoles = directRoles
                        InstallationRoles = installationRoles
                        Effective = can principal view capability resource
                    })

    let correctionCapability =
        function
        | RecordGroup.Registration -> Capability.Register
        | RecordGroup.Decision -> Capability.Decide
        | RecordGroup.Payment -> Capability.RecordPayment

    let correctionGroups =
        [ RecordGroup.Registration; RecordGroup.Decision; RecordGroup.Payment ]
        |> List.map (fun group -> group, correctionCapability group)

    let private correctionCapabilities = correctionGroups |> List.map snd

    let private capabilities kind =
        match (CaseActKind.describe kind).Capabilities with
        | ActCapabilities.Required values -> values
        | ActCapabilities.CorrectionGroups -> correctionCapabilities

    /// Reaffirmation is still a requested replacement and requires its group's capability.
    let requiredCapabilities =
        function
        | (CaseAct.Correct correction) ->
            [
                match correction.Registration with
                | Amendment.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Registration
                match correction.Decision with
                | Change.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Decision
                match correction.Payment with
                | PaymentChange.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Payment
            ]
        | act -> capabilities (CaseAct.kind act)

    let canAdvertise principal actor resource kind =
        match (CaseActKind.describe kind).Capabilities with
        | ActCapabilities.Required values ->
            values
            |> List.forall (fun capability -> can principal actor capability resource)
        | ActCapabilities.CorrectionGroups ->
            correctionCapabilities
            |> List.exists (fun capability -> can principal actor capability resource)

    /// An empty correction still needs an offered fact capability; concrete groups keep their own requirements.
    let missingCapabilities principal actor resource act =
        let required =
            match requiredCapabilities act with
            | [] when CaseAct.kind act = CaseActKind.Correct -> correctionCapabilities
            | values -> values

        required
        |> List.filter (fun capability -> not (can principal actor capability resource))

    let canAct principal actor resource act =
        canAdvertise principal actor resource (CaseAct.kind act)
        && (requiredCapabilities act
            |> List.forall (fun capability -> can principal actor capability resource))

    let visibility principal (view: AuthorityView) =
        if not (validActor principal view) then
            None
        elif can principal view Capability.ReadCase Scope.Installation then
            Some CaseVisibility.Every
        elif view.HasScopedCases then
            Some CaseVisibility.Scoped
        else
            None

    let private operationIdentity operationId =
        OperationId.create operationId
        |> Option.map Ok
        |> Option.defaultWith (fun () ->
            InputValidation.invalid InputTarget.OperationId Violation.OperationIdRequired)

    let private grantCount =
        function
        | AuthorityAct.Admit admission when
            admission.Grants.IsEmpty
            || admission.Grants.Length > AuthorityAdmission.MaximumGrants
            ->
            InputValidation.invalid
                InputTarget.Grants
                (Violation.GrantCountRange AuthorityAdmission.MaximumGrants)
        | _ -> Ok()

    let private scopeReference act =
        let grant (value: AuthoredGrant) =
            match value.Scope with
            | GrantTarget.CaseReference reference ->
                CaseReference.parse reference
                |> Result.map ignore
                |> Result.mapError (fun violation ->
                    Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation))
            | _ -> Ok()

        match act with
        | AuthorityAct.Grant value
        | AuthorityAct.Revoke value -> grant value
        | AuthorityAct.Admit admission when
            admission.Grants.Length <= AuthorityAdmission.MaximumGrants
            ->
            admission.Grants
            |> List.fold
                (fun state value -> Admission.map2 (fun () () -> ()) state (grant value))
                (Ok())
        | _ -> Ok()

    let private admitProfile input =
        ActorProfile.parse input
        |> Result.mapError (fun issues ->
            issues
            |> List.map (fun (field, violation) ->
                {
                    InputIssue.Target =
                        (match field with
                         | ActorProfileField.DisplayName -> InputTarget.ProfileDisplayName
                         | ActorProfileField.Contact -> InputTarget.ProfileContact)
                    Violation = violation
                    RelatedDate = None
                })
            |> InputIssues.ofList
            |> Option.defaultWith (fun () -> invalidOp "Profile errors are nonempty.")
            |> Refusal.InvalidInput)

    let private parsedPayload target act =
        let action = AuthorityAct.traverse Ok admitProfile act

        Admission.map2
            (fun principal action -> principal, action)
            (PrincipalKey.ofKind target.Kind target.Issuer target.Subject)
            action
        |> fun result -> Admission.map2 (fun value () -> value) result (grantCount act)
        |> fun result -> Admission.map2 (fun value () -> value) result (scopeReference act)

    let parse (request: AuthorityRequest) =
        match request.Act with
        | AuthorityAct.ProvisionOwner _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        | act ->
            Admission.map2
                (fun id (principal, action) ->
                    {
                        Id = id
                        Principal = principal
                        Action = action
                    })
                (operationIdentity request.OperationId)
                (parsedPayload request.Target act)

    let syntaxIntent operationId target act =
        match act with
        | AuthorityAct.ProvisionOwner _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        | _ ->
            Admission.collect
                [
                    operationId
                    |> Option.map (operationIdentity >> Result.map ignore)
                    |> Option.defaultValue (Ok())
                    parsedPayload target act |> Result.map ignore
                ]

    let provision operationId target profile =
        Admission.map2
            (fun id profile ->
                {
                    Id = id
                    Principal = target
                    Action = AuthorityAct.ProvisionOwner profile
                })
            (operationIdentity operationId)
            (admitProfile profile)

    let private owner (actor: ActorAuthority) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && List.contains
            {
                ActorGrant.Role = Role.Owner
                Scope = Scope.Installation
            }
            actor.Grants

    let private grantScope (facts: AuthorityFacts) target =
        match target with
        | GrantTarget.Installation -> Ok Scope.Installation
        | _ ->
            match Map.tryFind target facts.TargetCases with
            | Some id when
                (match target with
                 | GrantTarget.CaseId requested -> requested = id
                 | _ -> true)
                ->
                Ok(Scope.Case id)
            | _ -> Error AccessRefusal.ResourceUnavailable

    let private grantAdmission role scope principal =
        let kind =
            match scope with
            | Scope.Installation -> ScopeKind.Installation
            | Scope.Case _ -> ScopeKind.Case

        if humanOnlyRole role && not (PrincipalKey.isHuman principal) then
            Error AuthorityRefusal.HumanPrincipalRequired
        elif not (List.contains kind (roleScopes role)) then
            Error AuthorityRefusal.RoleScopeNotApplicable
        else
            Ok()

    let private grantChange (facts: AuthorityFacts) active role scope (actor: ActorAuthority) =
        if active then
            grantAdmission role scope actor.Principal
            |> Result.map (fun () -> AuthorityChange.Granted(role, scope))
        elif
            role = Role.Owner
            && scope = Scope.Installation
            && owner actor
            && required "enabled human owners" facts.EnabledHumanOwners <= 1
        then
            Error AuthorityRefusal.LastOwner
        else
            Ok(AuthorityChange.Revoked(role, scope))

    let private scopedChange facts active role target actor =
        grantScope facts target
        |> Result.mapError Refusal.Access
        |> Result.bind (fun scope ->
            grantChange facts active role scope actor |> Result.mapError Refusal.Authority)


    let private admissionChange
        facts
        principal
        (admission: AuthorityAdmission<AuthoredGrant, ActorProfile>)
        =
        let granted =
            admission.Grants
            |> List.fold
                (fun state grant ->
                    state
                    |> Result.bind (fun values ->
                        grantScope facts grant.Scope
                        |> Result.mapError Refusal.Access
                        |> Result.bind (fun scope ->
                            grantAdmission grant.Role scope principal
                            |> Result.mapError Refusal.Authority
                            |> Result.map (fun () ->
                                {
                                    Grant.Role = grant.Role
                                    Scope = scope
                                }
                                :: values))))
                (Ok [])

        granted
        |> Result.bind (fun grants ->
            let keys = grants |> List.map (fun grant -> grant.Role, grant.Scope)

            if (List.distinct keys).Length <> keys.Length then
                InputValidation.invalid InputTarget.Grants Violation.DuplicateGrant
            else
                Ok(AuthorityChange.Admitted(admission.Profile, List.rev grants)))

    let private disableChange facts actor =
        if owner actor && required "enabled human owners" facts.EnabledHumanOwners <= 1 then
            Error(Refusal.Authority AuthorityRefusal.LastOwner)
        else
            Ok AuthorityChange.Disabled

    let private existingChange facts act actor =
        match act with
        | AuthorityAct.SetProfile profile -> Ok(AuthorityChange.ProfileSet profile)
        | AuthorityAct.EraseProfile -> Ok AuthorityChange.ProfileErased
        | AuthorityAct.Enable -> Ok AuthorityChange.Enabled
        | AuthorityAct.Disable -> disableChange facts actor
        | AuthorityAct.Grant { Role = role; Scope = target } ->
            scopedChange facts true role target actor
        | AuthorityAct.Revoke { Role = role; Scope = target } ->
            scopedChange facts false role target actor

        | AuthorityAct.Register _
        | AuthorityAct.Admit _
        | AuthorityAct.ProvisionOwner _ ->
            Error(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)

    let private change (facts: AuthorityFacts) (command: AuthorityCommand) =
        match command.Act, facts.TargetActor with
        | AuthorityAct.ProvisionOwner _, _ when not (PrincipalKey.isHuman command.Target) ->
            Error(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired)
        | AuthorityAct.ProvisionOwner profile, None when required "actor count" facts.ActorCount = 0 ->
            Ok(AuthorityChange.OwnerProvisioned profile)
        | AuthorityAct.ProvisionOwner _, _
        | AuthorityAct.Register _, Some _
        | AuthorityAct.Admit _, Some _ ->
            Error(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
        | AuthorityAct.Register profile, None -> Ok(AuthorityChange.Registered profile)
        | AuthorityAct.Admit admission, None -> admissionChange facts command.Target admission
        | _, None -> Error(Refusal.Authority AuthorityRefusal.ActorUnknown)
        | act, Some actor -> existingChange facts act actor

    let decide (facts: AuthorityFacts) (command: AuthorityCommand) =
        let permitted =
            match command.Act, facts.Requester.Actor with
            | AuthorityAct.ProvisionOwner _, _ -> true
            | _, Some actor ->
                can actor.Principal facts.Requester Capability.ManageAuthority Scope.Installation
            | _, None -> false

        if permitted then
            change facts command
        else
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
