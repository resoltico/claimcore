namespace ClaimCore.Application.Storage

open System
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution

/// Restores a person from authenticated roots and its current projection.
module internal PersonAcquisition =
    let private corrupt message = raise (RecordCorruptException message)

    let person (tx: ReadTx) id =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT principal_kind,issuer,subject,enabled,display_name,contact,revision,display_name_search,contact_search,principal_key,digest_email FROM claimcore.actors WHERE actor_id=$1"
                    [| box (ActorId.value id) |]
                    tx.CancellationToken

            match rows with
            | [] -> return None
            | [ row ] ->
                let kind =
                    DatabaseSession.value<string> 0 row
                    |> Vocabulary.tryParse PrincipalKind.vocabulary
                    |> Option.defaultWith (fun () -> corrupt "Person principal kind is invalid.")

                let principal =
                    PrincipalKey.ofKind
                        kind
                        (DatabaseSession.value<string> 1 row)
                        (DatabaseSession.value<string> 2 row)
                    |> Result.defaultWith (fun _ -> corrupt "Person principal is invalid.")

                if DatabaseSession.value<string> 9 row <> PrincipalKey.naturalKey principal then
                    corrupt "Person principal key disagrees with its identity."

                let profile =
                    Projections.profileWithDigest
                        (DatabaseSession.optional<string> 4 row)
                        (DatabaseSession.optional<string> 5 row)
                        (DatabaseSession.optional<string> 10 row)

                let actor =
                    {
                        Actor.ActorId = id
                        Principal = principal
                        Enabled = DatabaseSession.value<bool> 3 row
                        Profile = profile
                    }

                let revision =
                    DatabaseSession.value<int64> 6 row |> DatabaseSession.admitted Revision.create

                let! grantRows =
                    DatabaseSession.rows
                        tx.Connection
                        "SELECT role,case_id,granted_sequence,granted_effect_index FROM claimcore.grants WHERE actor_id=$1"
                        [| box (ActorId.value id) |]
                        tx.CancellationToken

                let grants =
                    grantRows
                    |> List.map (fun row ->
                        let role =
                            DatabaseSession.value<string> 0 row
                            |> Vocabulary.tryParse Role.vocabulary
                            |> Option.defaultWith (fun () ->
                                corrupt "Person grant role is invalid.")

                        let scope =
                            DatabaseSession.optional<Guid> 1 row
                            |> Option.map (DatabaseSession.admitted CaseId.create >> Scope.Case)
                            |> Option.defaultValue Scope.Installation

                        let sequence =
                            DatabaseSession.value<int64> 2 row
                            |> DatabaseSession.admitted Sequence.create

                        {
                            ActorGrant.Role = role
                            Scope = scope
                        },
                        DatabaseSession.value<int> 3 row
                        |> DatabaseSession.admitted (EffectPosition.create sequence))
                    |> Map.ofList

                if grants.Count <> grantRows.Length then
                    corrupt "Person grant is duplicated."

                let! erasures =
                    DatabaseSession.rows
                        tx.Connection
                        "SELECT operation_sequence,effect_index FROM claimcore.ledger_effects WHERE erases_kind='PROFILE' AND erases_id=$1 ORDER BY operation_sequence DESC,effect_index DESC LIMIT 1"
                        [| box (ActorId.value id) |]
                        tx.CancellationToken

                let erasure =
                    if profile.IsSome then
                        None
                    else
                        match erasures with
                        | [ row ] ->
                            let sequence =
                                DatabaseSession.value<int64> 0 row
                                |> DatabaseSession.admitted Sequence.create

                            Some(
                                DatabaseSession.value<int> 1 row
                                |> DatabaseSession.admitted (EffectPosition.create sequence)
                            )
                        | _ -> corrupt "Missing profile has no accepted erasure."

                let! anchors =
                    DatabaseSession.rows
                        tx.Connection
                        "SELECT current_op.operation_id,current_part.effect_index,origin_op.operation_id,origin.effect_index,identity_op.operation_id,identity.effect_index FROM claimcore.ledger_effects current_part JOIN claimcore.ledger current_op ON current_op.sequence=current_part.operation_sequence JOIN claimcore.ledger_effects origin ON origin.part_kind=current_part.part_kind AND origin.part_id=current_part.part_id AND origin.revision=1 JOIN claimcore.ledger origin_op ON origin_op.sequence=origin.operation_sequence JOIN LATERAL (SELECT operation_sequence,effect_index FROM claimcore.ledger_effects WHERE part_kind=current_part.part_kind AND part_id=current_part.part_id AND retained ? 'principal' ORDER BY revision DESC LIMIT 1) identity ON true JOIN claimcore.ledger identity_op ON identity_op.sequence=identity.operation_sequence WHERE current_part.aggregate_kind='PERSON' AND current_part.aggregate_id=$1 AND current_part.part_id=$1 ORDER BY current_part.revision DESC LIMIT 1"
                        [| box (ActorId.value id) |]
                        tx.CancellationToken

                let anchor =
                    match anchors with
                    | [ row ] -> row
                    | _ -> corrupt "Person accepting anchors are absent."

                let! latest, cache, _ =
                    OperationStorage.acquire
                        tx
                        true
                        "Accepted part root is absent."
                        Map.empty
                        (DatabaseSession.value<Guid> 0 anchor
                         |> DatabaseSession.admitted OperationId.create)

                let! origin, cache, _ =
                    OperationStorage.acquire
                        tx
                        true
                        "Accepted part root is absent."
                        cache
                        (DatabaseSession.value<Guid> 2 anchor
                         |> DatabaseSession.admitted OperationId.create)

                let! identity, _, _ =
                    OperationStorage.acquire
                        tx
                        true
                        "Accepted part root is absent."
                        cache
                        (DatabaseSession.value<Guid> 4 anchor
                         |> DatabaseSession.admitted OperationId.create)

                let at (record: RetainedOperation) index =
                    record.Changes
                    |> List.tryItem (DatabaseSession.value<int> index anchor)
                    |> Option.defaultWith (fun () -> corrupt "Person accepting index is absent.")

                let person =
                    Person.restore
                        (at origin 3)
                        (at latest 1)
                        (at identity 5)
                        actor
                        revision
                        grants
                        erasure
                    |> Result.defaultWith (fun _ ->
                        corrupt "Person projection is inconsistent with its accepted anchors.")

                let _, _, _, _, name, contact =
                    Person.rows person
                    |> Result.defaultWith (fun _ -> corrupt "Person projection is incomplete.")

                if
                    DatabaseSession.optional<string> 7 row <> name
                    || DatabaseSession.optional<string> 8 row <> contact
                then
                    corrupt "Person search columns disagree with its profile."

                return Some person
            | _ -> return corrupt "Person identity is duplicated."
        }

    let principalOwner (tx: ReadTx) principal =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT part_id FROM claimcore.ledger_effects WHERE part_kind='PERSON' AND upper(natural_key COLLATE \"C\")=$1"
                    [| box ((PrincipalKey.naturalKey principal).ToUpperInvariant()) |]
                    tx.CancellationToken

            match rows with
            | [] -> return None
            | [ row ] ->
                return
                    Some(
                        DatabaseSession.value<Guid> 0 row |> DatabaseSession.admitted ActorId.create
                    )
            | _ -> return corrupt "A natural person key has multiple owners."
        }

    let principal (tx: ReadTx) principal =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT actor_id,principal_kind,issuer,subject FROM claimcore.actors WHERE principal_key=$1"
                    [| box (PrincipalKey.naturalKey principal) |]
                    tx.CancellationToken

            match rows with
            | [] -> return None
            | [ row ] ->
                let kind, issuer, subject = PrincipalKey.parts principal

                if
                    DatabaseSession.value<string> 1 row
                    <> Vocabulary.token PrincipalKind.vocabulary kind
                    || DatabaseSession.value<string> 2 row <> issuer
                    || DatabaseSession.value<string> 3 row <> subject
                then
                    corrupt "Principal key refers to a different identity."

                return!
                    person
                        tx
                        (DatabaseSession.value<Guid> 0 row
                         |> DatabaseSession.admitted ActorId.create)
            | _ -> return corrupt "Principal key is duplicated."
        }
