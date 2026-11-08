namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open System
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

[<NoEquality; NoComparison>]
type internal AuthorityFoldContext = { FindCase: string -> CaseId option }

/// Retained authority interpretation uses only earlier ledger state, never projections or today's matrix.
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

    let private target (actors: Map<ActorId, ActorAuthority>) (entry: StoredEntry) id =
        Map.tryFind id actors
        |> known (Some entry.Sequence) VerificationFinding.AuthorityFold

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
                            Profile = None
                        }
                    Grants = if provision then [ ownerGrant ] else []
                }

            return Map.add id actor actors
        }

    let private grant
        (context: AuthorityFoldContext)
        (entry: StoredEntry)
        (actor: ActorAuthority)
        (value: RecordedGrant)
        active
        =
        let findCase = context.FindCase
        let role = value.Role
        let scope = value.Scope

        result {
            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.HumanRequired
                    (not active
                     || not (role = Role.Owner || role = Role.DataSteward)
                     || PrincipalKey.isHuman actor.Principal)

            let! scope =
                match scope with
                | RecordedGrantScope.Installation -> Ok Scope.Installation
                | RecordedGrantScope.Case(id, reference) ->
                    require
                        (Some entry.Sequence)
                        VerificationFinding.AuthorityFold
                        (findCase (CaseReference.format reference) = Some id)
                    |> Result.map (fun () -> Scope.Case id)

            let grant: ActorGrant = { Role = role; Scope = scope }
            let grants = actor.Grants |> List.filter ((<>) grant)

            return
                { actor with
                    Grants = if active then grant :: grants else grants
                }
        }

    let private principal (entry: StoredEntry) (input: PrincipalInput) =
        PrincipalKey.ofKind input.Kind input.Issuer input.Subject
        |> Result.mapError (fun _ ->
            divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

    let private action
        (context: AuthorityFoldContext)
        actors
        (entry: StoredEntry)
        (record: AuthorityEffectRecord)
        =
        let id = record.TargetActorId

        let update change =
            target actors entry id
            |> Result.map (fun actor -> Map.add id (change actor) actors)

        match record.Effect with
        | AuthorityEffect.ProvisionOwner input ->
            principal entry input
            |> Result.bind (fun principal -> register actors entry id principal true)
        | AuthorityEffect.Register input ->
            principal entry input
            |> Result.bind (fun principal -> register actors entry id principal false)
        | AuthorityEffect.SetProfile profile ->
            update (fun actor ->
                { actor with
                    Actor =
                        { actor.Actor with
                            Profile = Some profile
                        }
                })
        | AuthorityEffect.EraseProfile ->
            update (fun actor ->
                { actor with
                    Actor = { actor.Actor with Profile = None }
                })
        | AuthorityEffect.Enable ->
            update (fun actor ->
                { actor with
                    Actor = { actor.Actor with Enabled = true }
                })
        | AuthorityEffect.Disable ->
            update (fun actor ->
                { actor with
                    Actor = { actor.Actor with Enabled = false }
                })
        | AuthorityEffect.Grant value
        | AuthorityEffect.Revoke value ->
            let active =
                match record.Effect with
                | AuthorityEffect.Grant _ -> true
                | _ -> false

            target actors entry id
            |> Result.bind (fun actor ->
                grant context entry actor value active
                |> Result.map (fun actor -> Map.add id actor actors))

    let apply
        _installationId
        findCase
        (actors: Map<ActorId, ActorAuthority>)
        (entry: StoredEntry)
        (record: AuthorityEffectRecord)
        =
        result {
            let! target, kind =
                match entry.Subject with
                | EntryTarget.Authority(id, kind) -> Ok(id, kind)
                | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.PayloadFormat
                    (record.OperationId = OperationId.value entry.OperationId
                     && record.TargetActorId = target
                     && AuthorityEffect.kind record.Effect = kind)

            do!
                match record.Effect with
                | AuthorityEffect.ProvisionOwner _ -> Ok()
                | _ -> executor actors entry true true

            let! next = action { FindCase = findCase } actors entry record

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.AuthorityFold
                    (next |> Map.values |> Seq.exists owner)

            return next
        }

    let erasedProfile (actors: Map<ActorId, ActorAuthority>) (entry: StoredEntry) =
        result {
            do! executor actors entry true true

            let! id =
                match entry.Subject with
                | EntryTarget.Authority(id, AuthorityKind.SetProfile) -> Ok id
                | _ -> Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)

            let! actor = target actors entry id

            return
                Map.add
                    id
                    { actor with
                        Actor = { actor.Actor with Profile = None }
                    }
                    actors
        }
