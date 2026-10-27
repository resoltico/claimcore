namespace ClaimCore.Domain

open System
open ClaimCore.Domain.ResultFlow

type ValidAuthorityAct = AuthorityAct<AuthoredGrant>

type AuthorityCommand =
    private
        {
            Id: Guid
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
            | Scope.Installation -> Some view.Installation
            | Scope.Case id when
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
                | Amendment.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Registration
                match correction.Decision with
                | Change.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Decision
                match correction.Payment with
                | Change.Keep -> ()
                | _ -> yield correctionCapability RecordGroup.Payment
            ]
        | act -> ordinaryActs (CaseAct.kind act)

    let canAdvertise principal actor resource kind =
        ordinaryActs kind
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
                    | AuthorityAct.Grant {
                                             Role = _
                                             Scope = GrantTarget.CaseReference reference
                                         }
                    | AuthorityAct.Revoke {
                                              Role = _
                                              Scope = GrantTarget.CaseReference reference
                                          } -> Some reference
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

    /// Restores structurally parsed permanent inputs without current authorization decisions.
    let restore operationId target (act: ValidAuthorityAct) = command operationId target act

    let parse (request: AuthorityRequest) =
        result {
            let! target =
                PrincipalKey.ofKind request.Target.Kind request.Target.Issuer request.Target.Subject

            let grant (value: AuthoredGrant) =
                Ok
                    {
                        AuthoredGrant.Role = value.Role
                        Scope = value.Scope
                    }

            let! act =
                match request.Act with
                | AuthorityAct.ProvisionOwner -> Error Refusal.ActNotPermitted
                | act -> AuthorityAct.traverse grant act

            return! command request.OperationId target act
        }

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

    let private grantChange (facts: AuthorityFacts) active role target (actor: ActorAuthority) =
        let scope =
            match target, facts.TargetCaseId with
            | GrantTarget.Installation, _ -> Ok Scope.Installation
            | GrantTarget.CaseReference _, Some id -> Ok(Scope.Case id)
            | _ -> Error Refusal.ResourceUnavailable

        scope
        |> Result.bind (fun scope ->
            if humanOnlyRole role && not (PrincipalKey.isHuman actor.Principal) then
                Error Refusal.HumanPrincipalRequired
            elif
                not active
                && role = Role.Owner
                && scope = Scope.Installation
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
        | AuthorityAct.ProvisionOwner, _ when not (PrincipalKey.isHuman command.Target) ->
            Error Refusal.HumanPrincipalRequired
        | AuthorityAct.ProvisionOwner, None when facts.ActorCount = 0 ->
            Ok AuthorityChange.OwnerProvisioned
        | AuthorityAct.ProvisionOwner, _
        | AuthorityAct.Register, Some _ -> Error Refusal.ActorAlreadyRegistered
        | AuthorityAct.Register, None -> Ok AuthorityChange.Registered
        | _, None -> Error Refusal.ActorUnknown
        | AuthorityAct.Disable, Some actor when owner actor && facts.EnabledHumanOwners <= 1 ->
            Error Refusal.LastOwner
        | AuthorityAct.Enable, Some _ -> Ok AuthorityChange.Enabled
        | AuthorityAct.Disable, Some _ -> Ok AuthorityChange.Disabled
        | AuthorityAct.Grant { Role = role; Scope = target }, Some actor ->
            grantChange facts true role target actor
        | AuthorityAct.Revoke { Role = role; Scope = target }, Some actor ->
            grantChange facts false role target actor

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
            Error Refusal.ResourceUnavailable
