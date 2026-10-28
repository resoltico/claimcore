namespace ClaimCore.Application

open ClaimCore.Domain

open System
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

[<NoEquality; NoComparison>]
type internal AuthorityFoldContext =
    {
        InstallationId: Guid
        CaseExists: CaseId -> bool
    }

type internal AuthorityFoldTarget =
    {
        ActorId: ActorId
        Principal: PrincipalKey
    }

/// Permanent authority interpretation uses only earlier ledger state, never projections or today's matrix.
module internal AuthorityFold =

    let private ownerGrant: ActorGrant =
        {
            Role = Role.Owner
            Scope = Scope.Installation
        }

    let private owner (actor: ActorAuthority) =
        actor.Enabled
        && PrincipalKey.isHuman actor.Principal
        && List.contains ownerGrant actor.Grants

    let executor (actors: Map<ActorId, ActorAuthority>) (entry: StoredEntry) human ownerRequired =
        result {
            let! actor =
                Map.tryFind entry.ActorId actors
                |> known (Some entry.Sequence) VerificationFinding.ActorNotEnabled

            do! require (Some entry.Sequence) VerificationFinding.ActorNotEnabled actor.Enabled

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.HumanRequired
                    (not human || PrincipalKey.isHuman actor.Principal)

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.OwnerRequired
                    (not ownerRequired || owner actor)

            return ()
        }

    let private principal (entry: StoredEntry) (request: AuthorityRequestRecord) =
        PrincipalKey.ofKind request.Target.Kind request.Target.Issuer request.Target.Subject
        |> Result.mapError (fun _ ->
            divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

    let private target (actors: Map<ActorId, ActorAuthority>) (entry: StoredEntry) id principal =
        result {
            let! actor =
                Map.tryFind id actors
                |> known (Some entry.Sequence) VerificationFinding.AuthorityFold

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (actor.Principal = principal)

            return actor
        }

    let private register
        (actors: Map<ActorId, ActorAuthority>)
        (entry: StoredEntry)
        id
        principal
        provision
        =
        result {
            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (id = ActorId.ofOperationId entry.OperationId
                     && not (Map.containsKey id actors)
                     && (actors
                         |> Map.values
                         |> Seq.forall (fun actor -> actor.Principal <> principal)))

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (not provision
                     || (Map.isEmpty actors && entry.Sequence = Sequence.first && entry.ActorId = id))

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.HumanRequired
                    (not provision || PrincipalKey.isHuman principal)

            let actor =
                {
                    Actor =
                        {
                            ActorId = id
                            Principal = principal
                            Enabled = true
                        }
                    Grants = if provision then [ ownerGrant ] else []
                }

            return Map.add id actor actors
        }

    let private grant
        (context: AuthorityFoldContext)
        (entry: StoredEntry)
        (actor: ActorAuthority)
        (value: AuthoredGrant)
        active
        =
        let installationId = context.InstallationId
        let caseExists = context.CaseExists
        let role = value.Role
        let scope = value.Scope

        result {
            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.HumanRequired
                    (not (Authority.humanOnlyRole role) || PrincipalKey.isHuman actor.Principal)

            let! scope =
                match scope with
                | GrantTarget.Installation -> Ok Scope.Installation
                | GrantTarget.CaseReference reference ->
                    let id = CaseReferenceId.encode installationId reference

                    require (Some entry.Sequence) VerificationFinding.AuthorityFold (caseExists id)
                    |> Result.map (fun () -> Scope.Case id)

            let grant: ActorGrant = { Role = role; Scope = scope }
            let grants = actor.Grants |> List.filter ((<>) grant)

            return
                { actor with
                    Grants = if active then grant :: grants else grants
                }
        }

    let private action
        (context: AuthorityFoldContext)
        (actors: Map<ActorId, ActorAuthority>)
        (entry: StoredEntry)
        (subject: AuthorityFoldTarget)
        request
        =
        let id = subject.ActorId
        let principal = subject.Principal

        match request.Act with
        | AuthorityAct.ProvisionOwner -> register actors entry id principal true
        | AuthorityAct.Register -> register actors entry id principal false
        | AuthorityAct.Enable ->
            target actors entry id principal
            |> Result.map (fun actor ->
                Map.add
                    id
                    { actor with
                        Actor = { actor.Actor with Enabled = true }
                    }
                    actors)
        | AuthorityAct.Disable ->
            target actors entry id principal
            |> Result.map (fun actor ->
                Map.add
                    id
                    { actor with
                        Actor = { actor.Actor with Enabled = false }
                    }
                    actors)
        | AuthorityAct.Grant value
        | AuthorityAct.Revoke value ->
            let active =
                match request.Act with
                | AuthorityAct.Grant _ -> true
                | _ -> false

            target actors entry id principal
            |> Result.bind (fun actor ->
                grant context entry actor value active
                |> Result.map (fun actor -> Map.add id actor actors))

    let apply
        installationId
        caseExists
        (actors: Map<ActorId, ActorAuthority>)
        (entry: StoredEntry)
        (request: AuthorityRequestRecord)
        =
        result {
            let! id, kind =
                match entry.Subject with
                | EntryTarget.Authority(id, kind) -> Ok(id, kind)
                | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.PayloadFormat
                    (request.OperationId = OperationId.value entry.OperationId
                     && AuthorityAct.kind request.Act = kind)

            let! principal = principal entry request

            do!
                if request.Act = AuthorityAct.ProvisionOwner then
                    Ok()
                else
                    executor actors entry true true

            let! next =
                action
                    {
                        InstallationId = installationId
                        CaseExists = caseExists
                    }
                    actors
                    entry
                    { ActorId = id; Principal = principal }
                    request

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (next |> Map.values |> Seq.exists owner)

            return next
        }
