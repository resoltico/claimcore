namespace ClaimCore.Postgres

open System
open ClaimCore.Domain

/// Complete rights and accepting provenance are loaded only for a new people decision.
module internal PeopleStateStorage =
    let load connection principal token =
        task {
            let! actors =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact,display_name_search,contact_search FROM claimcore.actors WHERE principal_kind=$1 AND issuer=$2 AND subject=$3"
                    (AccessRequests.parameters principal)
                    token

            match actors with
            | [] -> return None
            | [ row ] ->
                let actor = Projections.actorRow row
                let displayNameSearch, contactSearch = PeopleSubject.columns actor

                if
                    DatabaseSession.optional<string> 7 row <> displayNameSearch
                    || DatabaseSession.optional<string> 8 row <> contactSearch
                then
                    raise (
                        RecordCorruptException "People search columns disagree with their profile."
                    )

                let! grants =
                    DatabaseSession.rows
                        connection
                        "SELECT role,case_id,granted_sequence,granted_effect_index FROM claimcore.grants WHERE actor_id=$1"
                        [| box (ActorId.value actor.ActorId) |]
                        token

                let grantsWithPositions =
                    grants
                    |> List.map (fun row ->
                        let role =
                            DatabaseSession.value<string> 0 row
                            |> Vocabulary.tryParse Role.vocabulary
                            |> Option.defaultWith (fun () ->
                                raise (RecordCorruptException "Invalid people role."))

                        let scope =
                            DatabaseSession.optional<Guid> 1 row
                            |> Option.map (DatabaseSession.admitted CaseId.create >> Scope.Case)
                            |> Option.defaultValue Scope.Installation

                        let sequence =
                            DatabaseSession.value<int64> 2 row
                            |> DatabaseSession.admitted Sequence.create

                        let position =
                            DatabaseSession.value<int> 3 row
                            |> DatabaseSession.admitted (EffectPosition.create sequence)

                        {
                            ActorGrant.Role = role
                            Scope = scope
                        },
                        position)

                let indexed = Map.ofList grantsWithPositions

                if indexed.Count <> grants.Length then
                    raise (RecordCorruptException "Duplicate people grant rows.")

                let! erasures =
                    DatabaseSession.rows
                        connection
                        "SELECT operation_sequence,effect_index,act FROM claimcore.ledger_effects WHERE subject_kind='PEOPLE' AND subject_id=$1 AND act IN ('people.setProfile','people.eraseProfile') ORDER BY operation_sequence DESC,effect_index DESC LIMIT 1"
                        [| box (ActorId.value actor.ActorId) |]
                        token

                let erasure =
                    match erasures with
                    | [ row ] ->
                        let erased =
                            DatabaseSession.value<string> 2 row =
                                PeopleActKind.token PeopleActKind.EraseProfile

                        if actor.Profile.IsNone <> erased then
                            raise (
                                RecordCorruptException
                                    "Current profile presence disagrees with its retained source."
                            )

                        if erased then
                            let sequence =
                                DatabaseSession.value<int64> 0 row
                                |> DatabaseSession.admitted Sequence.create

                            Some(
                                DatabaseSession.value<int> 1 row
                                |> DatabaseSession.admitted (EffectPosition.create sequence)
                            )
                        else
                            None
                    | _ -> raise (RecordCorruptException "Registered actor has no profile source.")

                let state =
                    PeopleSubject.restore actor indexed erasure
                    |> Result.defaultWith (fun _ ->
                        raise (RecordCorruptException "Invalid owned people state."))

                if PeopleSubject.rows state |> Option.isNone then
                    raise (
                        RecordCorruptException
                            "A current profile is missing without its erasure source."
                    )

                return Some state
            | _ -> return raise (RecordCorruptException "Duplicate people principal rows.")
        }
