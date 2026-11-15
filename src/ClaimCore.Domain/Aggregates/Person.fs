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
