namespace ClaimCore.Domain.Aggregates

open ClaimCore.Domain
open ClaimCore.Domain.Changes

type Person =
    private
        {
            Identity: ActorId
            PrincipalValue: PrincipalKey
            EnabledValue: bool
            ProfileValue: ProfileValue
            RevisionValue: Revision
            GrantsValue: Map<ActorGrant, EffectPosition>
            ProfileErasureValue: EffectPosition option
        }

    member this.Actor =
        {
            ActorId = this.Identity
            Principal = this.PrincipalValue
            Enabled = this.EnabledValue
            Profile = ProfileValue.value this.ProfileValue
        }

    member this.Id = this.Identity
    member this.Profile = this.ProfileValue
    member this.Revision = this.RevisionValue
    member this.Grants = this.GrantsValue
    member this.ProfileErasure = this.ProfileErasureValue

module Person =
    let access (state: Person) =
        {
            ActorAccess.Actor = state.Actor
            Grants = state.Grants |> Map.keys |> Seq.toList
        }

    let apply (state: Person option) (change: PartChange) =
        let invalid = Error RecordViolation.InvalidSubjectState

        match change.Aggregate, change.Value with
        | AggregateReference.Person id, PartValue.Person value when
            change.PartId = ActorId.value id
            && PartChange.validErasure change
            && (change.Erases.IsNone || value.Profile = ProfileValue.Absent)
            ->
            let prior = state |> Option.map _.Revision |> Option.defaultValue Revision.zero

            if not (PartChange.successor prior change.Revision) then
                Error RecordViolation.InvalidRevisionSequence
            elif state |> Option.exists (fun state -> state.Id <> id) then
                invalid
            elif
                value.Grants |> List.map _.Grant |> List.distinct |> List.length
                <> value.Grants.Length
            then
                invalid
            elif
                change.Erases.IsNone
                && value.Profile = ProfileValue.Absent
                && (state |> Option.exists (fun state -> state.Profile <> ProfileValue.Absent))
            then
                invalid
            else
                let principal =
                    match state, value.Principal, value.Profile with
                    | None, Some principal, ProfileValue.Present _ when
                        change.NaturalKey = Some(PrincipalKey.naturalKey principal)
                        && id = ActorId.ofOperationId change.Stamp.OperationId
                        && change.Erases.IsNone
                        && value.Enabled
                        && List.forall _.Active value.Grants
                        ->
                        Some principal
                    | Some before, Some principal, _ when
                        let kind, _, _ = PrincipalKey.parts principal
                        let currentKind, _, _ = PrincipalKey.parts before.Actor.Principal

                        kind = currentKind
                        && principal <> before.Actor.Principal
                        && change.Erases.IsNone
                        && (change.NaturalKey
                            |> Option.forall ((=) (PrincipalKey.naturalKey principal)))
                        ->
                        Some principal
                    | Some before, None, _ when change.NaturalKey.IsNone ->
                        Some before.Actor.Principal
                    | _ -> None

                match principal with
                | None -> invalid
                | Some principal when
                    value.Grants
                    |> List.exists (fun assignment ->
                        assignment.Active
                        && (Access.grantRefusal principal assignment.Grant).IsSome)
                    ->
                    invalid
                | Some principal ->
                    let before = state |> Option.map _.Grants |> Option.defaultValue Map.empty

                    let grants =
                        value.Grants
                        |> List.fold
                            (fun current assignment ->
                                if assignment.Active then
                                    Map.add assignment.Grant change.Stamp.Position current
                                else
                                    Map.remove assignment.Grant current)
                            before

                    Ok
                        {
                            Identity = id
                            PrincipalValue = principal
                            EnabledValue = value.Enabled
                            ProfileValue = value.Profile
                            RevisionValue = change.Revision
                            GrantsValue = grants
                            ProfileErasureValue =
                                if change.Erases.IsSome then Some change.Stamp.Position
                                elif value.Profile <> ProfileValue.Absent then None
                                else state |> Option.bind _.ProfileErasure
                        }
        | _ -> invalid

    let rows (state: Person) =
        match state.Profile with
        | ProfileValue.Present None -> Error RecordViolation.InvalidSubjectState
        | ProfileValue.Absent when state.ProfileErasure.IsNone ->
            Error RecordViolation.InvalidSubjectState
        | _ ->
            let actor = state.Actor

            let displayName =
                actor.Profile
                |> Option.map (fun profile ->
                    PartyName.format profile.DisplayName |> TextSearch.normalize)

            let contact =
                actor.Profile
                |> Option.bind _.Contact
                |> Option.map (ContactText.format >> TextSearch.normalize)

            Ok(actor, state.Revision, state.Grants, state.ProfileErasure, displayName, contact)

    /// Current rows must agree with the immutable identity and latest accepted person value.
    let restore
        (origin: PartChange)
        (latest: PartChange)
        (identity: PartChange)
        (actor: Actor)
        revision
        (grants: Map<ActorGrant, EffectPosition>)
        (erasure: EffectPosition option)
        =
        let invalid = Error RecordViolation.InvalidSubjectState

        match origin.Value, latest.Value, identity.Value with
        | PartValue.Person created, PartValue.Person current, PartValue.Person identified ->
            let sameKind (other: PrincipalKey) =
                let kind, _, _ = PrincipalKey.parts actor.Principal
                let otherKind, _, _ = PrincipalKey.parts other
                kind = otherKind

            let valid =
                origin.Aggregate = AggregateReference.Person actor.ActorId
                && latest.Aggregate = origin.Aggregate
                && origin.PartId = ActorId.value actor.ActorId
                && latest.PartId = origin.PartId
                && origin.Revision = Revision.first
                && latest.Revision = revision
                && revision >= Revision.first
                && latest.Stamp.Position >= origin.Stamp.Position
                && actor.ActorId = ActorId.ofOperationId origin.Stamp.OperationId
                && (created.Principal
                    |> Option.exists (fun principal ->
                        sameKind principal
                        && origin.NaturalKey = Some(PrincipalKey.naturalKey principal)))
                && identity.Aggregate = origin.Aggregate
                && identity.PartId = origin.PartId
                && identity.Revision >= origin.Revision
                && identity.Revision <= latest.Revision
                && identity.Stamp.Position >= origin.Stamp.Position
                && identity.Stamp.Position <= latest.Stamp.Position
                && identified.Principal = Some actor.Principal
                && (identity.NaturalKey
                    |> Option.forall ((=) (PrincipalKey.naturalKey actor.Principal)))
                && origin.Erases.IsNone
                && created.Enabled
                && created.Profile <> ProfileValue.Absent
                && (if identity.Revision = latest.Revision then
                        identity = latest
                    else
                        current.Principal.IsNone && latest.NaturalKey.IsNone)
                && current.Enabled = actor.Enabled
                && ProfileValue.value current.Profile = actor.Profile
                && PartChange.validErasure latest
                && (current.Profile = ProfileValue.Absent) = erasure.IsSome
                && (erasure
                    |> Option.forall (fun position ->
                        position >= origin.Stamp.Position && position <= latest.Stamp.Position))
                && (latest.Erases.IsNone || erasure = Some latest.Stamp.Position)
                && (grants
                    |> Map.forall (fun _ position ->
                        position >= origin.Stamp.Position && position <= latest.Stamp.Position))
                && (current.Grants
                    |> List.forall (fun assignment ->
                        if assignment.Active then
                            Map.tryFind assignment.Grant grants = Some latest.Stamp.Position
                        else
                            not (Map.containsKey assignment.Grant grants)))

            if not valid then
                invalid
            else
                let state =
                    {
                        Identity = actor.ActorId
                        PrincipalValue = actor.Principal
                        EnabledValue = actor.Enabled
                        ProfileValue = current.Profile
                        RevisionValue = revision
                        GrantsValue = grants
                        ProfileErasureValue = erasure
                    }

                rows state |> Result.map (fun _ -> state)
        | _ -> invalid
