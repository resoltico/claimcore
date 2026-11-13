namespace ClaimCore.Domain

/// Rights and their accepting positions have one owner; access views derive from this map.
type PeopleSubjectState =
    {
        Actor: Actor
        Grants: Map<ActorGrant, EffectPosition>
        ProfileErasure: EffectPosition option
    }

type PeopleSubjectDependencies =
    {
        Requester: AccessView
        TargetCases: Map<GrantTarget, CaseId>
        EnabledHumanOwners: int option
        ActorCount: int option
    }

[<RequireQualifiedAccess>]
type PeopleAdmissionMode =
    | Current
    | Recorded

type PeopleSubjectEffect =
    {
        Change: PeopleChange
        After: ActorAccess
    }

/// Positions come from the closed accepted root, never from front-end choices.
type PeopleSubjectProvenance =
    {
        Granted: Map<ActorGrant, EffectPosition>
        ProfileErasure: EffectPosition option
    }

type PeopleSubjectRows =
    {
        Actor: Actor
        DisplayNameSearch: string option
        ContactSearch: string option
        Grants: Map<ActorGrant, EffectPosition>
        ProfileErasure: EffectPosition option
    }

module PeopleSubject =
    let access (state: PeopleSubjectState) : ActorAccess =
        {
            Actor = state.Actor
            Grants = state.Grants |> Map.keys |> Seq.toList
        }

    let private facts dependencies state =
        {
            PeopleFacts.TargetActor = state |> Option.map access
            TargetCases = dependencies.TargetCases
            EnabledHumanOwners = dependencies.EnabledHumanOwners
            ActorCount = dependencies.ActorCount
            Requester = dependencies.Requester
        }

    let restore actor grants erasure =
        let validScope (grant: ActorGrant) =
            let scope =
                match grant.Scope with
                | Scope.Installation -> ScopeKind.Installation
                | Scope.Case _ -> ScopeKind.Case

            Access.roleScopes grant.Role |> List.contains scope

        if grants |> Map.forall (fun grant _ -> validScope grant) then
            Ok
                {
                    PeopleSubjectState.Actor = actor
                    Grants = grants
                    ProfileErasure = erasure
                }
        else
            Error RecordViolation.InvalidSubjectState

    let decide
        mode
        (dependencies: PeopleSubjectDependencies)
        (state: PeopleSubjectState option)
        (command: PeopleCommand)
        =
        let admission =
            match mode with
            | PeopleAdmissionMode.Current -> People.admitPrepared command
            | PeopleAdmissionMode.Recorded -> Ok()

        let admission =
            if state |> Option.exists (fun state -> state.Actor.Principal <> command.Target) then
                Error(Refusal.Access AccessRefusal.ResourceUnavailable)
            else
                admission

        admission
        |> Result.bind (fun () -> People.decide (facts dependencies state) command)
        |> Result.map (fun change ->
            let target =
                state
                |> Option.map (fun state -> state.Actor.ActorId)
                |> Option.defaultValue (ActorId.ofOperationId command.OperationId)

            {
                Change = change
                After =
                    People.applyChange target command.Target (state |> Option.map access) change
            })

    let private applyAccess
        (provenance: PeopleSubjectProvenance)
        (state: PeopleSubjectState option)
        (after: ActorAccess)
        =
        let before = state |> Option.map _.Grants |> Option.defaultValue Map.empty
        let afterGrants = Set.ofList after.Grants
        let added = Set.difference afterGrants (before |> Map.keys |> Set.ofSeq)

        let identity =
            state
            |> Option.forall (fun state ->
                state.Actor.ActorId = after.Actor.ActorId
                && state.Actor.Principal = after.Actor.Principal)

        if not identity || added <> (provenance.Granted |> Map.keys |> Set.ofSeq) then
            Error RecordViolation.InvalidSubjectState
        else
            let retained = before |> Map.filter (fun grant _ -> Set.contains grant afterGrants)

            let grants =
                provenance.Granted
                |> Map.fold (fun grants grant position -> Map.add grant position grants) retained

            Ok(
                Some
                    {
                        PeopleSubjectState.Actor = after.Actor
                        Grants = grants
                        ProfileErasure =
                            if after.Actor.Profile.IsSome then
                                None
                            else
                                provenance.ProfileErasure
                                |> Option.orElse (state |> Option.bind _.ProfileErasure)
                    }
            )

    let apply provenance state (effect: PeopleSubjectEffect) =
        applyAccess provenance state effect.After

    /// Missing profile bytes retain structural registration/rights semantics; later erasure is proved by the ledger fold.
    let applyErased dependencies provenance state target operation principal act =
        People.replayRetained (facts dependencies state) target operation principal act
        |> Result.mapError (fun _ -> RecordViolation.InvalidSubjectState)
        |> Result.bind (applyAccess provenance state)

    let columns (actor: Actor) =
        actor.Profile
        |> Option.map (fun profile -> PartyName.format profile.DisplayName |> TextSearch.normalize),
        actor.Profile
        |> Option.bind _.Contact
        |> Option.map (ContactText.format >> TextSearch.normalize)

    let rows (state: PeopleSubjectState) =
        if state.Actor.Profile.IsNone && state.ProfileErasure.IsNone then
            None
        else
            let displayNameSearch, contactSearch = columns state.Actor

            Some
                {
                    Actor = state.Actor
                    DisplayNameSearch = displayNameSearch
                    ContactSearch = contactSearch
                    Grants = state.Grants
                    ProfileErasure = state.ProfileErasure
                }
