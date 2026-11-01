namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

type ValidAuthorityAct = AuthorityAct<AuthoredGrant>

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
    | Registered
    | OwnerProvisioned
    | Granted of Role * Scope
    | Revoked of Role * Scope
    | Enabled
    | Disabled

[<RequireQualifiedAccess>]
type AuthorityFact =
    | ActorCount
    | EnabledHumanOwners

type AuthorityFacts =
    {
        TargetActor: ActorAuthority option
        TargetCaseId: CaseId option
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
        | AuthorityAct.ProvisionOwner -> [ AuthorityFact.ActorCount ]
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
            Validation.invalid InputTarget.OperationId Violation.OperationIdRequired)

    let private scopeReference =
        function
        | AuthorityAct.Grant {
                                 Scope = GrantTarget.CaseReference reference
                             }
        | AuthorityAct.Revoke {
                                  Scope = GrantTarget.CaseReference reference
                              } ->
            CaseReference.parse reference
            |> Result.map ignore
            |> Result.mapError (fun violation ->
                Refusal.invalidInput (InputTarget.Field BusinessField.CaseReference, violation))
        | _ -> Ok()

    let private command operationId target act =
        Admission.map2
            (fun identity () ->
                {
                    Id = identity
                    Principal = target
                    Action = act
                })
            (operationIdentity operationId)
            (scopeReference act)

    /// Restores structurally parsed permanent inputs without current authorization decisions.
    let restore operationId target (act: ValidAuthorityAct) = command operationId target act

    let parse (request: AuthorityRequest) =
        match request.Act with
        | AuthorityAct.ProvisionOwner -> Error(Refusal.Access AccessRefusal.ActNotPermitted)
        | act ->
            Admission.map2
                (fun (identity, target) () ->
                    {
                        Id = identity
                        Principal = target
                        Action = act
                    })
                (Admission.map2
                    (fun identity target -> identity, target)
                    (operationIdentity request.OperationId)
                    (PrincipalKey.ofKind
                        request.Target.Kind
                        request.Target.Issuer
                        request.Target.Subject))
                (scopeReference act)

    let provision operationId target =
        command operationId target AuthorityAct.ProvisionOwner

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
        match target, facts.TargetCaseId with
        | GrantTarget.Installation, _ -> Ok Scope.Installation
        | GrantTarget.CaseReference _, Some id -> Ok(Scope.Case id)
        | GrantTarget.CaseId requested, Some id when requested = id -> Ok(Scope.Case id)
        | _ -> Error AccessRefusal.ResourceUnavailable

    let private grantAdmission role scope (actor: ActorAuthority) =
        let kind =
            match scope with
            | Scope.Installation -> ScopeKind.Installation
            | Scope.Case _ -> ScopeKind.Case

        if humanOnlyRole role && not (PrincipalKey.isHuman actor.Principal) then
            Error AuthorityRefusal.HumanPrincipalRequired
        elif not (List.contains kind (roleScopes role)) then
            Error AuthorityRefusal.RoleScopeNotApplicable
        else
            Ok()

    let private grantChange (facts: AuthorityFacts) active role scope (actor: ActorAuthority) =
        if active then
            grantAdmission role scope actor
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


    let private change (facts: AuthorityFacts) (command: AuthorityCommand) =
        match command.Act, facts.TargetActor with
        | AuthorityAct.ProvisionOwner, _ when not (PrincipalKey.isHuman command.Target) ->
            Error(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired)
        | AuthorityAct.ProvisionOwner, None when required "actor count" facts.ActorCount = 0 ->
            Ok AuthorityChange.OwnerProvisioned
        | AuthorityAct.ProvisionOwner, _
        | AuthorityAct.Register, Some _ ->
            Error(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
        | AuthorityAct.Register, None -> Ok AuthorityChange.Registered
        | _, None -> Error(Refusal.Authority AuthorityRefusal.ActorUnknown)
        | AuthorityAct.Disable, Some actor when
            owner actor && required "enabled human owners" facts.EnabledHumanOwners <= 1
            ->
            Error(Refusal.Authority AuthorityRefusal.LastOwner)
        | AuthorityAct.Enable, Some _ -> Ok AuthorityChange.Enabled
        | AuthorityAct.Disable, Some _ -> Ok AuthorityChange.Disabled
        | AuthorityAct.Grant { Role = role; Scope = target }, Some actor ->
            scopedChange facts true role target actor
        | AuthorityAct.Revoke { Role = role; Scope = target }, Some actor ->
            scopedChange facts false role target actor

    let decide (facts: AuthorityFacts) (command: AuthorityCommand) =
        let permitted =
            match command.Act, facts.Requester.Actor with
            | AuthorityAct.ProvisionOwner, _ -> true
            | _, Some actor ->
                can actor.Principal facts.Requester Capability.ManageAuthority Scope.Installation
            | _, None -> false

        if permitted then
            change facts command
        else
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
