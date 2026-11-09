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
    | OwnerProvisioned of ActorProfile * Role list
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
        [ RecordGroup.Registration; RecordGroup.Decision ]
        |> List.map (fun group -> group, correctionCapability group)

    let private correctionCapabilities = correctionGroups |> List.map snd

    let private capabilities kind =
        match (CaseActKind.describe kind).Capabilities with
        | ActCapabilities.Required values -> values
        | ActCapabilities.CorrectionGroups -> correctionCapabilities

    /// Only asserted correction groups require their business capabilities.
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
            |> List.mapi (fun index value ->
                grant value
                |> Result.mapError (function
                    | Refusal.InvalidInput issues ->
                        let ordinal = GrantOrdinal.create index |> Option.get
                        Refusal.InvalidInput(InputIssues.atGrantRow ordinal issues)
                    | refusal -> refusal))
            |> Admission.collect
        | _ -> Ok()

    let private profileIssues result =
        result
        |> Result.mapError (fun issues ->
            issues
            |> List.map (fun (field, violation) ->
                {
                    InputIssue.Target =
                        (match field with
                         | ActorProfileField.DisplayName -> InputTarget.ProfileDisplayName
                         | ActorProfileField.Contact -> InputTarget.ProfileContact)
                    Violation = violation
                    GrantRow = None
                    RelatedDate = None
                })
            |> InputIssues.ofList
            |> Option.defaultWith (fun () -> invalidOp "Profile errors are nonempty.")
            |> Refusal.InvalidInput)

    let private admitProfile input =
        ActorProfile.parse input |> profileIssues

    let private prepareProfile input =
        ActorProfile.restore input |> profileIssues

    let private parsedPayload profile target act =
        let action = AuthorityAct.traverse Ok profile act

        Admission.map2
            (fun principal action -> principal, action)
            (PrincipalKey.ofKind target.Kind target.Issuer target.Subject)
            action
        |> fun result -> Admission.map2 (fun value () -> value) result (grantCount act)
        |> fun result -> Admission.map2 (fun value () -> value) result (scopeReference act)

    let private prepareWith profile (request: AuthorityRequest) =
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
                (parsedPayload profile request.Target act)

    /// Permanent typed intent can be compared with accepted history before current policy.
    let prepare request = prepareWith prepareProfile request

    let parse request = prepareWith admitProfile request

    let admitPrepared (command: AuthorityCommand) =
        AuthorityAct.traverse Ok (ActorProfile.admit >> profileIssues) command.Act
        |> Result.map ignore

    let syntaxIntent operationId target act =
        match act with
        | AuthorityAct.ProvisionOwner _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        | _ ->
            Admission.collect
                [
                    operationId
                    |> Option.map (operationIdentity >> Result.map ignore)
                    |> Option.defaultValue (Ok())
                    parsedPayload prepareProfile target act |> Result.map ignore
                ]

    let initialOwnerRoles =
        [ Role.CaseHandler; Role.PaymentRecorder; Role.DataSteward; Role.Auditor ]

    let initialOwnerRole role = List.contains role initialOwnerRoles

    let validateInitialOwnerRoles roles =
        if List.distinct roles <> roles then
            Error(Refusal.invalidInput (InputTarget.Grants, Violation.DuplicateGrant))
        elif roles |> List.exists (initialOwnerRole >> not) then
            Error(Refusal.Authority AuthorityRefusal.RoleScopeNotApplicable)
        else
            Ok roles

    let provision operationId target profile roles =
        Admission.map2
            (fun (id, profile) roles ->
                {
                    Id = id
                    Principal = target
                    Action = AuthorityAct.ProvisionOwner(profile, roles)
                })
            (Admission.map2
                (fun id profile -> id, profile)
                (operationIdentity operationId)
                (prepareProfile profile))
            (validateInitialOwnerRoles roles)

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


    let private admissionGrants facts principal (grants: AuthoredGrant list) =
        let granted =
            grants
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
                Ok(List.rev grants))

    let private admissionChange
        facts
        principal
        (admission: AuthorityAdmission<AuthoredGrant, ActorProfile>)
        =
        admissionGrants facts principal admission.Grants
        |> Result.map (fun grants -> AuthorityChange.Admitted(admission.Profile, grants))

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

    /// Identity registration is decided independently of its erasable profile effect.
    /// Execution and replay use this same check, including for a profile later erased.
    let decideRegistration (facts: AuthorityFacts) principal provision =
        let permitted =
            provision
            || (facts.Requester.Actor
                |> Option.exists (fun actor ->
                    can
                        actor.Principal
                        facts.Requester
                        Capability.ManageAuthority
                        Scope.Installation))

        if not permitted then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        elif provision && not (PrincipalKey.isHuman principal) then
            Error(Refusal.Authority AuthorityRefusal.HumanPrincipalRequired)
        elif
            facts.TargetActor.IsSome
            || (provision && required "actor count" facts.ActorCount <> 0)
        then
            Error(Refusal.Authority AuthorityRefusal.ActorAlreadyRegistered)
        else
            Ok()

    let registeredActor id principal provision : ActorAuthority =
        {
            Actor =
                {
                    ActorId = id
                    Principal = principal
                    Enabled = true
                    Profile = None
                }
            Grants =
                if provision then
                    [
                        {
                            Role = Role.Owner
                            Scope = Scope.Installation
                        }
                    ]
                else
                    []
        }

    /// A decided authority change produces its next actor state.
    let applyChange id principal (before: ActorAuthority option) change =
        let current () =
            before
            |> Option.defaultWith (fun () -> invalidOp "A decided change requires its actor.")

        let profile value (actor: ActorAuthority) =
            { actor with
                Actor = { actor.Actor with Profile = value }
            }

        match change with
        | AuthorityChange.Registered value ->
            registeredActor id principal false |> profile (Some value)
        | AuthorityChange.OwnerProvisioned(value, roles) ->
            let actor = registeredActor id principal true |> profile (Some value)

            { actor with
                Grants =
                    actor.Grants
                    @ (roles
                       |> List.map (fun role ->
                           {
                               Grant.Role = role
                               Scope = Scope.Installation
                           }))
            }
        | AuthorityChange.Admitted(value, grants) ->
            { registeredActor id principal false with
                Grants = grants
            }
            |> profile (Some value)
        | AuthorityChange.ProfileSet value -> current () |> profile (Some value)
        | AuthorityChange.ProfileErased -> current () |> profile None
        | AuthorityChange.Enabled ->
            let actor = current () in

            { actor with
                Actor = { actor.Actor with Enabled = true }
            }
        | AuthorityChange.Disabled ->
            let actor = current () in

            { actor with
                Actor = { actor.Actor with Enabled = false }
            }
        | AuthorityChange.Granted(role, scope) ->
            let actor = current ()
            let grant = { Role = role; Scope = scope }

            { actor with
                Grants = grant :: (actor.Grants |> List.filter ((<>) grant))
            }
        | AuthorityChange.Revoked(role, scope) ->
            let actor = current ()

            { actor with
                Grants =
                    actor.Grants
                    |> List.filter (fun grant -> grant.Role <> role || grant.Scope <> scope)
            }

    let private change (facts: AuthorityFacts) (command: AuthorityCommand) =
        match command.Act with
        | AuthorityAct.ProvisionOwner(profile, roles) ->
            decideRegistration facts command.Target true
            |> Result.bind (fun () -> validateInitialOwnerRoles roles)
            |> Result.map (fun roles -> AuthorityChange.OwnerProvisioned(profile, roles))
        | AuthorityAct.Register profile ->
            decideRegistration facts command.Target false
            |> Result.map (fun () -> AuthorityChange.Registered profile)
        | AuthorityAct.Admit admission ->
            decideRegistration facts command.Target false
            |> Result.bind (fun () -> admissionChange facts command.Target admission)
        | act ->
            match facts.TargetActor with
            | None -> Error(Refusal.Authority AuthorityRefusal.ActorUnknown)
            | Some actor -> existingChange facts act actor

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

    /// Stored typed inputs obey permanent structure; current profile assertion policy is not re-parsed.
    let restoreCommand operation principal (act: ValidAuthorityAct) =
        grantCount act
        |> Result.bind (fun () -> scopeReference act)
        |> Result.map (fun () ->
            {
                Id = operation
                Principal = principal
                Action = act
            })

    /// Replay retains public authority semantics after profile erasure. Absence means unavailable
    /// accepted bytes, never a substitute profile. The caller must prove the later accepted erasure.
    let replayRetained
        (facts: AuthorityFacts)
        target
        operation
        principal
        (act: AuthorityAct<AuthoredGrant, ActorProfile option>)
        =
        let permitted () =
            facts.Requester.Actor
            |> Option.exists (fun actor ->
                can actor.Principal facts.Requester Capability.ManageAuthority Scope.Installation)

        let profilelessRegistration provision grants =
            result {
                do! decideRegistration facts principal provision
                let! grants = grants

                return
                    { registeredActor target principal provision with
                        Grants = (registeredActor target principal provision).Grants @ grants
                    }
            }

        let noProfile =
            match act with
            | AuthorityAct.Register None -> Some(profilelessRegistration false (Ok []))
            | AuthorityAct.Admit { Profile = None; Grants = grants } ->
                Some(
                    grantCount act
                    |> Result.bind (fun () ->
                        profilelessRegistration false (admissionGrants facts principal grants))
                )
            | AuthorityAct.ProvisionOwner(None, roles) ->
                Some(
                    validateInitialOwnerRoles roles
                    |> Result.bind (fun roles ->
                        profilelessRegistration
                            true
                            (Ok(
                                roles
                                |> List.map (fun role ->
                                    {
                                        Role = role
                                        Scope = Scope.Installation
                                    })
                            )))
                )
            | AuthorityAct.SetProfile None ->
                Some(
                    if not (permitted ()) then
                        Error(Refusal.Access AccessRefusal.ResourceUnavailable)
                    else
                        match facts.TargetActor with
                        | Some actor when actor.Principal = principal ->
                            Ok
                                { actor with
                                    Actor = { actor.Actor with Profile = None }
                                }
                        | _ -> Error(Refusal.Authority AuthorityRefusal.ActorUnknown)
                )
            | _ -> None

        match noProfile with
        | Some result -> result
        | None ->
            AuthorityAct.traverse
                Ok
                (function
                | Some value -> Ok value
                | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable))
                act
            |> Result.bind (restoreCommand operation principal)
            |> Result.bind (fun command -> decide facts command)
            |> Result.map (applyChange target principal facts.TargetActor)
