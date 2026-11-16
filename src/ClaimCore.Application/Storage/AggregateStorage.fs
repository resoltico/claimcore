namespace ClaimCore.Application.Storage

open System
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat

/// Restores authenticated public roots and only the private values needed by this consumer.
module internal AggregateStorage =
    let private corrupt message = raise (RecordCorruptException message)

    let private operation (tx: ReadTx) cache id =
        task {
            match Map.tryFind id cache with
            | Some record -> return record, cache
            | None ->
                let! record =
                    OperationStorage.find tx.Connection tx.InstallationId id tx.CancellationToken

                let record =
                    record |> Option.defaultWith (fun () -> corrupt "Accepted part root is absent.")

                return record, Map.add id record cache
        }

    let private readFamily includePrivate (tx: ReadTx) id =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    ("SELECT DISTINCT ON(e.part_kind,e.part_id) r.operation_id,e.effect_index,o.operation_id,f.effect_index,"
                     + DatabaseSession.dateExpression "m.due"
                     + " FROM claimcore.ledger_effects e JOIN claimcore.ledger r ON r.sequence=e.operation_sequence JOIN claimcore.ledger_effects f ON f.part_kind=e.part_kind AND f.part_id=e.part_id AND f.revision=1 JOIN claimcore.ledger o ON o.sequence=f.operation_sequence LEFT JOIN claimcore.reminders m ON e.part_kind='REMINDER' AND m.reminder_id=e.part_id WHERE e.aggregate_kind='FAMILY' AND e.aggregate_id=$1 ORDER BY e.part_kind,e.part_id,e.revision DESC")
                    [| box (CaseId.value id) |]
                    tx.CancellationToken

            if rows.IsEmpty then
                return None
            else
                let mutable cache = Map.empty
                let mutable parts = []
                let mutable origins = Map.empty
                let mutable dates = Map.empty
                let mutable publicCache = Map.empty

                let load privateValues id =
                    task {
                        match Map.tryFind id cache with
                        | Some record -> return record
                        | None when privateValues ->
                            let! record, loaded = operation tx cache id
                            cache <- loaded
                            return record
                        | None ->
                            match Map.tryFind id publicCache with
                            | Some record -> return record
                            | None ->
                                let! record =
                                    OperationStorage.findStructure
                                        tx.Connection
                                        tx.InstallationId
                                        id
                                        tx.CancellationToken

                                let record =
                                    record
                                    |> Option.defaultWith (fun () ->
                                        corrupt "Accepted part root is absent.")

                                publicCache <- Map.add id record publicCache
                                return record
                    }

                for row in rows do
                    let idAt index =
                        DatabaseSession.value<Guid> index row
                        |> DatabaseSession.admitted OperationId.create

                    let! structure = load false (idAt 0)
                    let index = DatabaseSession.value<int> 1 row

                    let retained =
                        structure.Changes
                        |> List.tryItem index
                        |> Option.defaultWith (fun () -> corrupt "Accepted part index is absent.")

                    let! record = load (includePrivate retained.Kind retained.PartId) (idAt 0)
                    let! first = load false (idAt 2)

                    let part =
                        record.Changes
                        |> List.tryItem (DatabaseSession.value<int> 1 row)
                        |> Option.defaultWith (fun () -> corrupt "Accepted part index is absent.")

                    let origin =
                        first.Changes
                        |> List.tryItem (DatabaseSession.value<int> 3 row)
                        |> Option.defaultWith (fun () -> corrupt "Part origin index is absent.")

                    if
                        part.Aggregate <> AggregateReference.Family id
                        || origin.Aggregate <> part.Aggregate
                        || origin.Kind <> part.Kind
                        || origin.PartId <> part.PartId
                        || origin.Revision <> Revision.first
                    then
                        corrupt "Accepted part origin does not match its current owner."

                    match part.Value with
                    | PartValue.Reminder _ ->
                        match DatabaseSession.optionalCalendarDate 4 row with
                        | Some due ->
                            let reminder = DatabaseSession.admitted ReminderId.create part.PartId
                            dates <- Map.add reminder (CalendarDate.ofDate due) dates
                        | None -> ()
                    | _ -> ()

                    parts <- part :: parts
                    origins <- Map.add (part.Kind, part.PartId) origin.Stamp origins

                let! identities =
                    DatabaseSession.rows
                        tx.Connection
                        "SELECT natural_key FROM claimcore.ledger_effects WHERE aggregate_kind='FAMILY' AND aggregate_id=$1 AND natural_key IS NOT NULL"
                        [| box (CaseId.value id) |]
                        tx.CancellationToken

                let reference =
                    match identities with
                    | [ row ] ->
                        DatabaseSession.value<string> 0 row
                        |> CaseReference.parse
                        |> Result.defaultWith (fun _ -> corrupt "Family reference is invalid.")
                    | _ -> corrupt "Family has no unique creation identity."

                let sequence =
                    parts |> List.map (_.Stamp.Position >> EffectPosition.sequence) |> List.max

                let family =
                    CaseFamily.restoreForDecision
                        dates
                        includePrivate
                        id
                        reference
                        sequence
                        parts
                        origins
                    |> Result.defaultWith (fun _ ->
                        corrupt "Accepted family parts are inconsistent.")

                return Some(family, parts, origins)
        }

    /// Aggregate consumers need note identity/removal and reminder dates, not unrelated text.
    let family tx id =
        readFamily (fun kind _ -> kind <> PartKind.Note && kind <> PartKind.Reminder) tx id

    /// Reminder lists offer controls for their rows and therefore also need the recorded plans.
    let familyForReminders tx id =
        readFamily (fun kind _ -> kind <> PartKind.Note) tx id

    let familyForDecision tx id binding =
        readFamily
            (fun kind part ->
                match kind, binding with
                | PartKind.Note, Binding.Note(_, note) -> part = NoteId.value note
                | PartKind.Note, _ -> false
                | PartKind.Reminder, Binding.Reminder(_, reminder, _) ->
                    part = ReminderId.value reminder
                | PartKind.Reminder, _ -> false
                | _ -> true)
            tx
            id

    let person (tx: ReadTx) id =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    "SELECT principal_kind,issuer,subject,enabled,display_name,contact,revision,display_name_search,contact_search,principal_key FROM claimcore.actors WHERE actor_id=$1"
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
                    match
                        DatabaseSession.optional<string> 4 row,
                        DatabaseSession.optional<string> 5 row
                    with
                    | None, None -> None
                    | Some displayName, contact ->
                        ActorProfile.restore
                            {
                                DisplayName = displayName
                                Contact = contact
                            }
                        |> Result.map Some
                        |> Result.defaultWith (fun _ -> corrupt "Person profile is invalid.")
                    | _ -> corrupt "Person profile is incomplete."

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
                        "SELECT l.operation_id,e.effect_index,o.operation_id,f.effect_index FROM claimcore.ledger_effects e JOIN claimcore.ledger l ON l.sequence=e.operation_sequence JOIN claimcore.ledger_effects f ON f.part_kind=e.part_kind AND f.part_id=e.part_id AND f.revision=1 JOIN claimcore.ledger o ON o.sequence=f.operation_sequence WHERE e.aggregate_kind='PERSON' AND e.aggregate_id=$1 AND e.part_id=$1 ORDER BY e.revision DESC LIMIT 1"
                        [| box (ActorId.value id) |]
                        tx.CancellationToken

                let anchor =
                    match anchors with
                    | [ row ] -> row
                    | _ -> corrupt "Person accepting anchors are absent."

                let! latest, cache =
                    operation
                        tx
                        Map.empty
                        (DatabaseSession.value<Guid> 0 anchor
                         |> DatabaseSession.admitted OperationId.create)

                let! origin, _ =
                    operation
                        tx
                        cache
                        (DatabaseSession.value<Guid> 2 anchor
                         |> DatabaseSession.admitted OperationId.create)

                let at (record: RetainedOperation) index =
                    record.Changes
                    |> List.tryItem (DatabaseSession.value<int> index anchor)
                    |> Option.defaultWith (fun () -> corrupt "Person accepting index is absent.")

                let person =
                    Person.restore (at origin 3) (at latest 1) actor revision grants erasure
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

    let resolveCase (tx: ReadTx) selector =
        task {
            let key, identity =
                match selector with
                | CaseSelector.Id id -> None, Some id
                | CaseSelector.Reference raw ->
                    CaseReference.parse raw |> Result.toOption |> Option.map CaseReference.identity,
                    None

            if key.IsNone && identity.IsNone then
                return None
            else
                let! rows =
                    DatabaseSession.rows
                        tx.Connection
                        "SELECT DISTINCT aggregate_id FROM claimcore.ledger_effects WHERE aggregate_kind='FAMILY' AND (($1::uuid IS NOT NULL AND aggregate_id=$1) OR ($2::text IS NOT NULL AND upper(natural_key COLLATE \"C\")=$2))"
                        [|
                            DatabaseSession.parameter (identity |> Option.map CaseId.value)
                            DatabaseSession.parameter key
                        |]
                        tx.CancellationToken

                match rows with
                | [] -> return None
                | [ row ] ->
                    return
                        Some(
                            DatabaseSession.value<Guid> 0 row
                            |> DatabaseSession.admitted CaseId.create
                        )
                | _ -> return corrupt "Family selection is ambiguous."
        }
