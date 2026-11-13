namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

type ValidPeopleAct = PeopleAct<AuthoredGrant, ActorProfile>

type PeopleCommand =
    private
        {
            Id: OperationId
            Principal: PrincipalKey
            Action: ValidPeopleAct
        }

    member this.OperationId = this.Id
    member this.Target = this.Principal
    member this.Act = this.Action

[<RequireQualifiedAccess>]
type PeopleChange =
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
type PeopleFact =
    | ActorCount
    | EnabledHumanOwners

type PeopleFacts =
    {
        TargetActor: ActorAccess option
        TargetCases: Map<GrantTarget, CaseId>
        EnabledHumanOwners: int option
        ActorCount: int option
        Requester: AccessView
    }

module People =
    open Access

    /// Only these acts need installation-wide protection facts; callers load them in the action snapshot.
    let factsNeeded (act: ValidPeopleAct) =
        match act with
        | PeopleAct.ProvisionOwner _ -> [ PeopleFact.ActorCount ]
        | PeopleAct.Disable
        | PeopleAct.Revoke {
                               Role = Role.Owner
                               Scope = GrantTarget.Installation
                           } -> [ PeopleFact.EnabledHumanOwners ]
        | _ -> []

    let private required name value =
        value
        |> Option.defaultWith (fun () -> invalidOp ("Missing people protection fact: " + name))

    let private operationIdentity operationId =
        OperationId.create operationId
        |> Option.map Ok
        |> Option.defaultWith (fun () ->
            InputValidation.invalid InputTarget.OperationId Violation.OperationIdRequired)

    let private grantCount =
        function
        | PeopleAct.Admit admission when
            admission.Grants.Length < PeopleAdmission.MinimumGrants
            || admission.Grants.Length > PeopleAdmission.MaximumGrants
            ->
            InputValidation.invalid
                InputTarget.Grants
                (Violation.GrantCountRange PeopleAdmission.MaximumGrants)
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
        | PeopleAct.Grant value
        | PeopleAct.Revoke value -> grant value
        | PeopleAct.Admit admission when admission.Grants.Length <= PeopleAdmission.MaximumGrants ->
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
        let action = PeopleAct.traverse Ok profile act

        Admission.map2
            (fun principal action -> principal, action)
            (PrincipalKey.ofKind target.Kind target.Issuer target.Subject)
            action
        |> fun result -> Admission.map2 (fun value () -> value) result (grantCount act)
        |> fun result -> Admission.map2 (fun value () -> value) result (scopeReference act)

    let private prepareWith profile (request: PeopleRequest) =
        match request.Act with
        | PeopleAct.ProvisionOwner _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
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

    let admitPrepared (command: PeopleCommand) =
        PeopleAct.traverse Ok (ActorProfile.admit >> profileIssues) command.Act
        |> Result.map ignore

    let syntaxIntent operationId target act =
        match act with
        | PeopleAct.ProvisionOwner _ -> Error(Refusal.Access AccessRefusal.ResourceUnavailable)
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
            Error(Refusal.People PeopleRefusal.RoleScopeNotApplicable)
        else
            Ok roles

    let provision operationId target profile roles =
        Admission.map2
            (fun (id, profile) roles ->
                {
                    Id = id
                    Principal = target
                    Action = PeopleAct.ProvisionOwner(profile, roles)
                })
            (Admission.map2
                (fun id profile -> id, profile)
                (operationIdentity operationId)
                (prepareProfile profile))
            (validateInitialOwnerRoles roles)

    let private owner (actor: ActorAccess) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && List.contains
            {
                ActorGrant.Role = Role.Owner
                Scope = Scope.Installation
            }
            actor.Grants

    let private grantScope (facts: PeopleFacts) target =
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
            Error PeopleRefusal.HumanPrincipalRequired
        elif not (List.contains kind (roleScopes role)) then
            Error PeopleRefusal.RoleScopeNotApplicable
        else
            Ok()

    let private grantChange (facts: PeopleFacts) active role scope (actor: ActorAccess) =
        if active then
            grantAdmission role scope actor.Principal
            |> Result.map (fun () -> PeopleChange.Granted(role, scope))
        elif
            role = Role.Owner
            && scope = Scope.Installation
            && owner actor
            && required "enabled human owners" facts.EnabledHumanOwners <= 1
        then
            Error PeopleRefusal.LastOwner
        else
            Ok(PeopleChange.Revoked(role, scope))

    let private scopedChange facts active role target actor =
        grantScope facts target
        |> Result.mapError Refusal.Access
        |> Result.bind (fun scope ->
            grantChange facts active role scope actor |> Result.mapError Refusal.People)


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
                            |> Result.mapError Refusal.People
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
        (admission: PeopleAdmission<AuthoredGrant, ActorProfile>)
        =
        admissionGrants facts principal admission.Grants
        |> Result.map (fun grants -> PeopleChange.Admitted(admission.Profile, grants))

    let private disableChange facts actor =
        if owner actor && required "enabled human owners" facts.EnabledHumanOwners <= 1 then
            Error(Refusal.People PeopleRefusal.LastOwner)
        else
            Ok PeopleChange.Disabled

    let private existingChange facts act actor =
        match act with
        | PeopleAct.SetProfile profile -> Ok(PeopleChange.ProfileSet profile)
        | PeopleAct.EraseProfile -> Ok PeopleChange.ProfileErased
        | PeopleAct.Enable -> Ok PeopleChange.Enabled
        | PeopleAct.Disable -> disableChange facts actor
        | PeopleAct.Grant { Role = role; Scope = target } ->
            scopedChange facts true role target actor
        | PeopleAct.Revoke { Role = role; Scope = target } ->
            scopedChange facts false role target actor

        | PeopleAct.Register _
        | PeopleAct.Admit _
        | PeopleAct.ProvisionOwner _ -> Error(Refusal.People PeopleRefusal.ActorAlreadyRegistered)

    /// Identity registration is decided independently of its erasable profile effect.
    /// Execution and replay use this same check, including for a profile later erased.
    let decideRegistration (facts: PeopleFacts) principal provision =
        let permitted =
            provision
            || (facts.Requester.Actor
                |> Option.exists (fun actor ->
                    can actor.Principal facts.Requester Capability.ManageAccess Scope.Installation))

        if not permitted then
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)
        elif provision && not (PrincipalKey.isHuman principal) then
            Error(Refusal.People PeopleRefusal.HumanPrincipalRequired)
        elif
            facts.TargetActor.IsSome
            || (provision && required "actor count" facts.ActorCount <> 0)
        then
            Error(Refusal.People PeopleRefusal.ActorAlreadyRegistered)
        else
            Ok()

    let registeredActor id principal provision : ActorAccess =
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

    /// A decided people change produces its next actor state.
    let applyChange id principal (before: ActorAccess option) change =
        let current () =
            before
            |> Option.defaultWith (fun () -> invalidOp "A decided change requires its actor.")

        let profile value (actor: ActorAccess) =
            { actor with
                Actor = { actor.Actor with Profile = value }
            }

        match change with
        | PeopleChange.Registered value ->
            registeredActor id principal false |> profile (Some value)
        | PeopleChange.OwnerProvisioned(value, roles) ->
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
        | PeopleChange.Admitted(value, grants) ->
            { registeredActor id principal false with
                Grants = grants
            }
            |> profile (Some value)
        | PeopleChange.ProfileSet value -> current () |> profile (Some value)
        | PeopleChange.ProfileErased -> current () |> profile None
        | PeopleChange.Enabled ->
            let actor = current () in

            { actor with
                Actor = { actor.Actor with Enabled = true }
            }
        | PeopleChange.Disabled ->
            let actor = current () in

            { actor with
                Actor = { actor.Actor with Enabled = false }
            }
        | PeopleChange.Granted(role, scope) ->
            let actor = current ()
            let grant = { Role = role; Scope = scope }

            { actor with
                Grants = grant :: (actor.Grants |> List.filter ((<>) grant))
            }
        | PeopleChange.Revoked(role, scope) ->
            let actor = current ()

            { actor with
                Grants =
                    actor.Grants
                    |> List.filter (fun grant -> grant.Role <> role || grant.Scope <> scope)
            }

    let private change (facts: PeopleFacts) (command: PeopleCommand) =
        match command.Act with
        | PeopleAct.ProvisionOwner(profile, roles) ->
            decideRegistration facts command.Target true
            |> Result.bind (fun () -> validateInitialOwnerRoles roles)
            |> Result.map (fun roles -> PeopleChange.OwnerProvisioned(profile, roles))
        | PeopleAct.Register profile ->
            decideRegistration facts command.Target false
            |> Result.map (fun () -> PeopleChange.Registered profile)
        | PeopleAct.Admit admission ->
            decideRegistration facts command.Target false
            |> Result.bind (fun () -> admissionChange facts command.Target admission)
        | act ->
            match facts.TargetActor with
            | None -> Error(Refusal.People PeopleRefusal.ActorUnknown)
            | Some actor -> existingChange facts act actor

    let decide (facts: PeopleFacts) (command: PeopleCommand) =
        let permitted =
            match command.Act, facts.Requester.Actor with
            | PeopleAct.ProvisionOwner _, _ -> true
            | _, Some actor ->
                can actor.Principal facts.Requester Capability.ManageAccess Scope.Installation
            | _, None -> false

        if permitted then
            change facts command
        else
            Error(Refusal.Access AccessRefusal.ResourceUnavailable)

    /// Stored typed inputs obey permanent structure; current profile assertion policy is not re-parsed.
    let restoreCommand operation principal (act: ValidPeopleAct) =
        grantCount act
        |> Result.bind (fun () -> scopeReference act)
        |> Result.map (fun () ->
            {
                Id = operation
                Principal = principal
                Action = act
            })

    /// Replay retains public people semantics after profile erasure. Absence means unavailable
    /// accepted bytes, never a substitute profile. The caller must prove the later accepted erasure.
    let replayRetained
        (facts: PeopleFacts)
        target
        operation
        principal
        (act: PeopleAct<AuthoredGrant, ActorProfile option>)
        =
        let permitted () =
            facts.Requester.Actor
            |> Option.exists (fun actor ->
                can actor.Principal facts.Requester Capability.ManageAccess Scope.Installation)

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
            | PeopleAct.Register None -> Some(profilelessRegistration false (Ok []))
            | PeopleAct.Admit { Profile = None; Grants = grants } ->
                Some(
                    grantCount act
                    |> Result.bind (fun () ->
                        profilelessRegistration false (admissionGrants facts principal grants))
                )
            | PeopleAct.ProvisionOwner(None, roles) ->
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
            | PeopleAct.SetProfile None ->
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
                        | _ -> Error(Refusal.People PeopleRefusal.ActorUnknown)
                )
            | _ -> None

        match noProfile with
        | Some result -> result
        | None ->
            PeopleAct.traverse
                Ok
                (function
                | Some value -> Ok value
                | None -> Error(Refusal.Access AccessRefusal.ResourceUnavailable))
                act
            |> Result.bind (restoreCommand operation principal)
            |> Result.bind (fun command -> decide facts command)
            |> Result.map (applyChange target principal facts.TargetActor)
