namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

[<RequireQualifiedAccess>]
type AuthorityCommandAct =
    | Register
    | Grant of Role * GrantTarget
    | Revoke of Role * GrantTarget
    | Enable
    | Disable
    | ProvisionOwner

type AuthorityCommand =
    private
        {
            Id: Guid
            Principal: PrincipalKey
            Action: AuthorityCommandAct
        }

    member this.OperationId = this.Id
    member this.Target = this.Principal
    member this.Act = this.Action

[<RequireQualifiedAccess>]
type AuthorityChange =
    | Registered
    | OwnerProvisioned
    | Granted of Role * GrantScope
    | Revoked of Role * GrantScope
    | Enabled
    | Disabled

type AuthorityFacts =
    {
        TargetActor: ActorAuthority option
        TargetCaseId: Guid option
        EnabledHumanOwners: int
        ActorCount: int
        Requester: AuthorityView
    }

module Authority =
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

    /// Principal restrictions follow the capabilities a role grants.
    let humanOnlyCapability =
        function
        | Capability.Steward
        | Capability.ManageAuthority -> true
        | Capability.ReadCase
        | Capability.ReadHistory
        | Capability.ReadHistoryFull
        | Capability.Register
        | Capability.Decide
        | Capability.RecordPayment
        | Capability.Close
        | Capability.ReadAuthority -> false

    let humanOnlyRole role =
        policy role |> List.exists humanOnlyCapability

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
            actor.ActorId <> Guid.Empty
            && actor.Enabled
            && actor.Principal = principal
            && (PrincipalKey.isHuman principal || loaded |> List.forall (humanOnlyRole >> not)))

    let can principal (view: AuthorityView) capability resource =
        let scoped =
            match resource with
            | ResourceScope.Installation -> Some view.Installation
            | ResourceScope.Case id when
                id <> Guid.Empty
                && capability <> Capability.ManageAuthority
                && capability <> Capability.ReadAuthority
                ->
                Some(view.Installation @ (view.Cases |> Map.tryFind id |> Option.defaultValue []))
            | _ -> None

        validActor principal view
        && (not (humanOnlyCapability capability) || PrincipalKey.isHuman principal)
        && (scoped
            |> Option.exists (List.exists (fun role -> List.contains capability (policy role))))

    let correctionCapability =
        function
        | RecordGroup.Registration -> Capability.Register
        | RecordGroup.Decision -> Capability.Decide
        | RecordGroup.Payment -> Capability.RecordPayment

    let correctionGroups =
        [ RecordGroup.Registration; RecordGroup.Decision; RecordGroup.Payment ]
        |> List.map (fun group -> group, correctionCapability group)

    let private correctionCapabilities = correctionGroups |> List.map snd

    let private ordinaryActs =
        function
        | CaseActKind.Open
        | CaseActKind.AmendRegistration -> [ Capability.Register ]
        | CaseActKind.Decide
        | CaseActKind.WithdrawDecision -> [ Capability.Decide ]
        | CaseActKind.RecordPayment
        | CaseActKind.ClearPayment -> [ Capability.RecordPayment ]
        | CaseActKind.Close
        | CaseActKind.Reopen -> [ Capability.Close ]
        | CaseActKind.Void
        | CaseActKind.Reinstate
        | CaseActKind.Erase -> [ Capability.Steward ]
        | CaseActKind.Correct -> correctionCapabilities

    /// Reaffirmation is still a requested replacement and requires its group's capability.
    let requiredCapabilities =
        function
        | (CaseAct.Correct correction) ->
            [
                match correction.Registration with
                | RegistrationChange.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Registration
                match correction.Decision with
                | DecisionChange.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Decision
                match correction.Payment with
                | PaymentChange.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Payment
            ]
        | act -> ordinaryActs (CaseActs.kind act)

    let canAdvertise principal actor resource kind =
        ordinaryActs kind
        |> List.exists (fun capability -> can principal actor capability resource)

    let canAct principal actor resource act =
        canAdvertise principal actor resource (CaseActs.kind act)
        && (requiredCapabilities act
            |> List.forall (fun capability -> can principal actor capability resource))

    let visibility principal (view: AuthorityView) =
        if not (validActor principal view) then
            None
        elif can principal view Capability.ReadCase ResourceScope.Installation then
            Some CaseVisibility.Every
        elif view.HasAssignedCases then
            Some CaseVisibility.Assigned
        else
            None

    let private command operationId target act =
        result {
            if operationId = Guid.Empty then
                return!
                    Error(
                        Refusal.InvalidInput(InputTarget.OperationId, Violation.OperationIdRequired)
                    )
            else
                let reference =
                    match act with
                    | AuthorityCommandAct.Grant(_, GrantTarget.CaseReference reference)
                    | AuthorityCommandAct.Revoke(_, GrantTarget.CaseReference reference) ->
                        Some reference
                    | _ -> None

                do!
                    reference
                    |> Option.map (
                        CaseReference.parse
                        >> Result.map ignore
                        >> Result.mapError (fun violation ->
                            Refusal.InvalidInput(
                                InputTarget.Field BusinessField.CaseReference,
                                violation
                            ))
                    )
                    |> Option.defaultValue (Ok())

                return
                    {
                        Id = operationId
                        Principal = target
                        Action = act
                    }
        }

    let parse (request: AuthorityRequest) =
        result {
            let! target =
                PrincipalKey.admit request.Target.Kind request.Target.Issuer request.Target.Subject

            let grant make (value: GrantChange) =
                Role.parse value.Role
                |> Option.map (fun role -> make (role, value.Scope) |> Ok)
                |> Option.defaultValue (
                    Error(
                        Refusal.InvalidInput(
                            InputTarget.Role,
                            if String.IsNullOrWhiteSpace value.Role then
                                Violation.TextRequired
                            else
                                Violation.RoleFormat
                        )
                    )
                )

            let! act =
                match request.Act with
                | AuthorityAct.Register -> Ok AuthorityCommandAct.Register
                | AuthorityAct.Grant value -> grant AuthorityCommandAct.Grant value
                | AuthorityAct.Revoke value -> grant AuthorityCommandAct.Revoke value
                | AuthorityAct.Enable -> Ok AuthorityCommandAct.Enable
                | AuthorityAct.Disable -> Ok AuthorityCommandAct.Disable

            return! command request.OperationId target act
        }

    let provision operationId target =
        command operationId target AuthorityCommandAct.ProvisionOwner

    let private owner (actor: ActorAuthority) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && List.contains
            {
                Role = Role.Owner
                Scope = GrantScope.Installation
            }
            actor.Grants

    let private grantChange (facts: AuthorityFacts) active role target (actor: ActorAuthority) =
        let scope =
            match target, facts.TargetCaseId with
            | GrantTarget.Installation, _ -> Ok GrantScope.Installation
            | GrantTarget.CaseReference _, Some id -> Ok(GrantScope.Case id)
            | _ -> Error Refusal.ResourceUnavailable

        scope
        |> Result.bind (fun scope ->
            if humanOnlyRole role && not (PrincipalKey.isHuman actor.Principal) then
                Error Refusal.HumanPrincipalRequired
            elif
                not active
                && role = Role.Owner
                && scope = GrantScope.Installation
                && owner actor
                && facts.EnabledHumanOwners <= 1
            then
                Error Refusal.LastOwner
            elif active then
                Ok(AuthorityChange.Granted(role, scope))
            else
                Ok(AuthorityChange.Revoked(role, scope)))


    let private change (facts: AuthorityFacts) (command: AuthorityCommand) =
        match command.Act, facts.TargetActor with
        | AuthorityCommandAct.ProvisionOwner, _ when not (PrincipalKey.isHuman command.Target) ->
            Error Refusal.HumanPrincipalRequired
        | AuthorityCommandAct.ProvisionOwner, None when facts.ActorCount = 0 ->
            Ok AuthorityChange.OwnerProvisioned
        | AuthorityCommandAct.ProvisionOwner, _
        | AuthorityCommandAct.Register, Some _ -> Error Refusal.ActorAlreadyRegistered
        | AuthorityCommandAct.Register, None -> Ok AuthorityChange.Registered
        | _, None -> Error Refusal.ActorUnknown
        | AuthorityCommandAct.Disable, Some actor when owner actor && facts.EnabledHumanOwners <= 1 ->
            Error Refusal.LastOwner
        | AuthorityCommandAct.Enable, Some _ -> Ok AuthorityChange.Enabled
        | AuthorityCommandAct.Disable, Some _ -> Ok AuthorityChange.Disabled
        | AuthorityCommandAct.Grant(role, target), Some actor ->
            grantChange facts true role target actor
        | AuthorityCommandAct.Revoke(role, target), Some actor ->
            grantChange facts false role target actor

    let decide (facts: AuthorityFacts) (command: AuthorityCommand) =
        let permitted =
            match command.Act, facts.Requester.Actor with
            | AuthorityCommandAct.ProvisionOwner, _ -> true
            | _, Some actor ->
                can
                    actor.Principal
                    facts.Requester
                    Capability.ManageAuthority
                    ResourceScope.Installation
            | _, None -> false

        if permitted then
            change facts command
        else
            Error Refusal.ResourceUnavailable
