namespace ClaimCore.Application.Storage

open System
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution

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

    /// Acquisition evidence is internal: counts describe materialized records, not SQL scan effort.
    type AcquisitionShape =
        {
            MaterializedRows: int
            RootReads: int
            PrivateRootReads: int
            RoundTrips: int
        }

    type FamilyAcquisition =
        {
            Facts: DecisionFacts
            Parts: PartChange list
            Origins: Map<PartKind * Guid, SubjectStamp>
            Shape: AcquisitionShape
        }

    type private FamilyRootAcquisition =
        {
            Reference: CaseReference
            Sequence: Sequence
            Parts: PartChange list
            Origins: Map<PartKind * Guid, SubjectStamp>
            Projected: StoredCase
            Shape: AcquisitionShape
        }

    /// Receipt visibility consumes accepted revisions, never current child decision projections.
    type FamilyDisclosureState =
        {
            Snapshot: CaseSnapshot
            Revisions: Map<PartKind * Guid, Revision>
        }

    type private Selection =
        {
            Payments: OperationId list
            Reminders: ReminderId list
            Notes: NoteId list
            Attachments: AttachmentId list
            Parties: PartyId list
            Links: bool
            Operation: OperationId option
        }

    let private emptySelection =
        {
            Payments = []
            Reminders = []
            Notes = []
            Attachments = []
            Parties = []
            Links = false
            Operation = None
        }


    let private readFamilyRoots (selection: Selection) (tx: ReadTx) id =
        task {
            let mutable materialized = 0
            let mutable roundTrips = 0
            let mutable rootReads = 0
            let mutable privateRoots = 0

            let rows sql parameters =
                task {
                    roundTrips <- roundTrips + 1

                    let! values =
                        DatabaseSession.rows tx.Connection sql parameters tx.CancellationToken

                    materialized <- materialized + values.Length
                    return values
                }

            let paymentIds =
                selection.Payments @ (selection.Operation |> Option.toList) |> List.distinct

            let reminderIds =
                selection.Reminders
                @ (selection.Operation |> Option.map ReminderId.ofOperationId |> Option.toList)
                |> List.distinct

            let noteIds =
                selection.Notes
                @ (selection.Operation |> Option.map NoteId.ofOperationId |> Option.toList)
                |> List.distinct

            let! located =
                rows
                    "SELECT DISTINCT ON(e.part_kind,e.part_id) r.operation_id,e.effect_index,o.operation_id,f.effect_index,e.part_kind,e.part_id,e.revision,(SELECT max(s.operation_sequence) FROM claimcore.ledger_effects s WHERE s.aggregate_kind=$2 AND s.aggregate_id=$1) FROM claimcore.ledger_effects e JOIN claimcore.ledger r ON r.sequence=e.operation_sequence LEFT JOIN claimcore.ledger_effects f ON f.part_kind=e.part_kind AND f.part_id=e.part_id AND f.revision=1 LEFT JOIN claimcore.ledger o ON o.sequence=f.operation_sequence WHERE e.aggregate_kind=$2 AND e.aggregate_id=$1 AND (e.part_kind=ANY($3::text[]) OR (e.part_kind=$7 AND e.part_id=ANY($4::uuid[])) OR (e.part_kind=$8 AND e.part_id=ANY($5::uuid[])) OR (e.part_kind=$9 AND e.part_id=ANY($6::uuid[])) OR (e.part_kind=$11 AND (e.part_id=ANY($10::uuid[]) OR EXISTS(SELECT 1 FROM claimcore.case_parties p WHERE p.case_id=$1 AND p.party_id=e.part_id AND NOT p.removed AND p.role IS NOT NULL))) OR ($13 AND e.part_kind=$12 AND NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects later WHERE later.part_kind=e.part_kind AND later.part_id=e.part_id AND later.revision>e.revision) AND e.retained->>'removed'='false') OR (e.part_kind=$14 AND e.part_id=ANY($15::uuid[]))) ORDER BY e.part_kind,e.part_id,e.revision DESC"
                    [|
                        box (CaseId.value id)
                        box "FAMILY"
                        box [| PartKind.token PartKind.Case; PartKind.token PartKind.Work |]
                        box (paymentIds |> List.map OperationId.value |> List.toArray)
                        box (reminderIds |> List.map ReminderId.value |> List.toArray)
                        box (noteIds |> List.map NoteId.value |> List.toArray)
                        box (PartKind.token PartKind.Payment)
                        box (PartKind.token PartKind.Reminder)
                        box (PartKind.token PartKind.Note)
                        box (selection.Parties |> List.map PartyId.value |> List.toArray)
                        box (PartKind.token PartKind.Party)
                        box (PartKind.token PartKind.Link)
                        box selection.Links
                        box (PartKind.token PartKind.Attachment)
                        box (selection.Attachments |> List.map AttachmentId.value |> List.toArray)
                    |]

            let! projected = FamilyReads.findCase tx id
            roundTrips <- roundTrips + 1
            materialized <- materialized + (if projected.IsSome then 1 else 0)

            match located, projected with
            | [], None -> return None
            | [], Some _
            | _, None -> return corrupt "Current case root and projection presence disagree."
            | _, Some projected ->
                let mutable cache: Map<OperationId, RetainedOperation * bool> = Map.empty

                let load includePrivate operation =
                    task {
                        match Map.tryFind operation cache with
                        | Some(value, loadedPrivate) when loadedPrivate || not includePrivate ->
                            return value
                        | _ ->
                            let! value =
                                if includePrivate then
                                    OperationStorage.find
                                        tx.Connection
                                        tx.InstallationId
                                        operation
                                        tx.CancellationToken
                                else
                                    OperationStorage.findStructure
                                        tx.Connection
                                        tx.InstallationId
                                        operation
                                        tx.CancellationToken

                            let value =
                                value
                                |> Option.defaultWith (fun () ->
                                    corrupt "Selected accepted root is absent.")

                            cache <- Map.add operation (value, includePrivate) cache
                            rootReads <- rootReads + 1

                            if includePrivate then
                                privateRoots <- privateRoots + 1

                            roundTrips <- roundTrips + 3

                            materialized <-
                                materialized
                                + 1
                                + value.Changes.Length
                                + value.Root.Structure.Compartments.Length

                            return value
                    }

                let mutable parts = []
                let mutable origins = Map.empty

                for row in located do
                    let idAt index =
                        DatabaseSession.value<Guid> index row
                        |> DatabaseSession.admitted OperationId.create

                    let! record = load true (idAt 0)
                    let! first = load false (idAt 2)

                    let part =
                        record.Changes
                        |> List.tryItem (DatabaseSession.value<int> 1 row)
                        |> Option.defaultWith (fun () -> corrupt "Selected effect index is absent.")

                    let origin =
                        first.Changes
                        |> List.tryItem (DatabaseSession.value<int> 3 row)
                        |> Option.defaultWith (fun () -> corrupt "Selected origin index is absent.")

                    if
                        part.Aggregate <> AggregateReference.Family id
                        || origin.Aggregate <> part.Aggregate
                        || origin.Kind <> part.Kind
                        || origin.PartId <> part.PartId
                        || origin.Revision <> Revision.first
                        || PartKind.token part.Kind <> DatabaseSession.value<string> 4 row
                        || part.PartId <> DatabaseSession.value<Guid> 5 row
                        || Revision.value part.Revision <> DatabaseSession.value<int64> 6 row
                    then
                        corrupt
                            "Selected accepted part/origin disagrees with its owner and revision."

                    parts <- part :: parts
                    origins <- Map.add (part.Kind, part.PartId) origin.Stamp origins

                let sequence =
                    DatabaseSession.value<int64> 7 located.Head
                    |> DatabaseSession.admitted Sequence.create

                return
                    Some
                        {
                            Reference = projected.Snapshot.Reference
                            Sequence = sequence
                            Parts = parts
                            Origins = origins
                            Projected = projected
                            Shape =
                                {
                                    MaterializedRows = materialized
                                    RootReads = rootReads
                                    PrivateRootReads = privateRoots
                                    RoundTrips = roundTrips
                                }
                        }
        }

    let private readFamily (selection: Selection) (tx: ReadTx) id =
        task {
            let! loaded = readFamilyRoots selection tx id

            match loaded with
            | None -> return None
            | Some roots ->
                let mutable materialized = roots.Shape.MaterializedRows
                let mutable roundTrips = roots.Shape.RoundTrips
                let rootReads = roots.Shape.RootReads
                let privateRoots = roots.Shape.PrivateRootReads
                let reference = roots.Reference
                let sequence = roots.Sequence
                let parts = roots.Parts
                let origins = roots.Origins
                let projected = roots.Projected

                let namedRows sql parameters =
                    task {
                        roundTrips <- roundTrips + 1

                        let! values =
                            DatabaseSession.namedRows
                                tx.Connection
                                sql
                                parameters
                                tx.CancellationToken

                        materialized <- materialized + values.Length
                        return values
                    }

                let paymentIds =
                    selection.Payments @ (selection.Operation |> Option.toList) |> List.distinct

                let reminderIds =
                    selection.Reminders
                    @ (selection.Operation |> Option.map ReminderId.ofOperationId |> Option.toList)
                    |> List.distinct

                let noteIds =
                    selection.Notes
                    @ (selection.Operation |> Option.map NoteId.ofOperationId |> Option.toList)
                    |> List.distinct

                let! aggregate =
                    namedRows
                        ("SELECT count(*) AS payment_count,"
                         + "(SELECT "
                         + DatabaseSession.dateExpression "min(due)"
                         + " FROM claimcore.reminders WHERE case_id=$1 AND state=$2 AND snapshot IS NOT NULL) AS next_due,"
                         + "(SELECT "
                         + DatabaseSession.dateExpression "min(due)"
                         + " FROM claimcore.reminders WHERE case_id=$1 AND state=$2 AND snapshot IS NOT NULL AND NOT(reminder_id=ANY($3::uuid[]))) AS remaining_due,"
                         + "(SELECT "
                         + DatabaseSession.dateExpression "min(due)"
                         + " FROM claimcore.reminders WHERE case_id=$1 AND state=$2 AND kind=$4) AS next_deadline,"
                         + "(SELECT "
                         + DatabaseSession.dateExpression "min(due)"
                         + " FROM claimcore.reminders WHERE case_id=$1 AND state=$2 AND kind=$4 AND NOT(reminder_id=ANY($3::uuid[]))) AS remaining_deadline "
                         + "FROM claimcore.case_payments WHERE case_id=$1 AND NOT removed AND payment_date IS NOT NULL")
                        [|
                            box (CaseId.value id)
                            box (ReminderState.token ReminderState.Pending)
                            box (selection.Reminders |> List.map ReminderId.value |> List.toArray)
                            box (ReminderKind.token ReminderKind.Deadline)
                        |]

                let aggregate =
                    aggregate
                    |> List.tryExactlyOne
                    |> Option.defaultWith (fun () -> corrupt "Case aggregate facts are absent.")

                let date name =
                    DatabaseSession.namedOptionalCalendarDate name aggregate
                    |> Option.map CalendarDate.ofDate

                let! totals = PaymentStorage.totals tx.Connection (Some id) tx.CancellationToken
                roundTrips <- roundTrips + 1

                materialized <-
                    materialized
                    + (Map.tryFind id totals |> Option.map Map.count |> Option.defaultValue 0)

                let summary =
                    {
                        LiveFamilySummary.PaymentCount =
                            DatabaseSession.namedValue<int64> "payment_count" aggregate
                        PaymentTotals = Map.tryFind id totals |> Option.defaultValue Map.empty
                        NextPendingDue = date "next_due"
                        RemainingPendingDue = date "remaining_due"
                        NextDeadlineDue = date "next_deadline"
                        RemainingDeadlineDue = date "remaining_deadline"
                    }

                let facts =
                    DecisionFacts.restore
                        id
                        reference
                        sequence
                        parts
                        origins
                        summary
                        {
                            FamilyFactScope.Payments = Set.ofList paymentIds
                            Reminders = Set.ofList reminderIds
                            Notes = Set.ofList noteIds
                            Attachments = Set.ofList selection.Attachments
                            Parties = Set.ofList selection.Parties
                            Operation = selection.Operation
                        }
                    |> Result.defaultWith (fun _ ->
                        corrupt "Selected decision facts disagree with current root aggregates.")

                let _, index =
                    DecisionFacts.rows facts []
                    |> Result.defaultWith (fun _ ->
                        corrupt "Selected decision facts do not form a current index.")

                if
                    DecisionFacts.snapshot facts <> Some projected.Snapshot
                    || index <> projected.Index
                then
                    corrupt
                        "Consumed case projection disagrees with its accepted root/work/due facts."

                let! work = WorkReads.findWork tx id
                roundTrips <- roundTrips + 1
                materialized <- materialized + (if work.IsSome then 1 else 0)

                let expectedWork =
                    facts.Work
                    |> Option.bind (fun part ->
                        part.Value
                        |> Option.map (fun value ->
                            {
                                CaseWorkSnapshot.CaseId = id
                                Revision = part.Revision
                                Work = value
                            }))

                let expectedWork =
                    match facts.Case.Value.Lifecycle, facts.Work with
                    | Lifecycle.Erased, _ -> None
                    | _, None ->
                        Some
                            {
                                CaseId = id
                                Revision = Revision.zero
                                Work = CaseWork.empty
                            }
                    | _ -> expectedWork

                match work with
                | Some work when
                    work.Revision =
                        (facts.Work |> Option.map _.Revision |> Option.defaultValue Revision.zero)
                    && work.Snapshot = expectedWork
                    && work.ChangedAt =
                        (facts.Work |> Option.map (fun part -> part.Stamp.RecordedAt))
                    && work.RecordedBy =
                        (facts.Work
                         |> Option.map (fun part -> ProjectionRows.familyActor part.Stamp.Authority))
                    ->
                    ()
                | _ -> corrupt "Consumed work projection disagrees with its accepted root."

                let! paymentRows =
                    namedRows
                        ($"SELECT {PaymentStorage.columns},accepting_sequence,effect_index,removed FROM claimcore.case_payments WHERE case_id=$1 AND payment_id=ANY($2::uuid[])")
                        [|
                            box (CaseId.value id)
                            box (paymentIds |> List.map OperationId.value |> List.toArray)
                        |]

                if paymentRows.Length <> facts.Payments.Count then
                    corrupt "Selected payment root/projection presence disagrees."

                for row in paymentRows do
                    let payment =
                        DatabaseSession.namedValue<Guid> "payment_id" row
                        |> DatabaseSession.admitted OperationId.create

                    let part =
                        Map.tryFind payment facts.Payments
                        |> Option.defaultWith (fun () ->
                            corrupt "Selected payment projection has no accepted root.")

                    let origin = Map.find (PartKind.Payment, OperationId.value payment) origins

                    let entry =
                        DatabaseSession.namedOptional<DateOnly> "payment_date" row
                        |> Option.map (fun _ -> PaymentStorage.row row)

                    if
                        DatabaseSession.namedValue<int64> "accepting_sequence" row
                        <> Sequence.value (EffectPosition.sequence origin.Position)
                        || DatabaseSession.namedValue<int> "effect_index" row
                           <> EffectPosition.index origin.Position
                        || entry <> part.Value.Entry
                        || DatabaseSession.namedValue<bool> "removed" row <> part.Value.Removed
                    then
                        corrupt "Selected payment values/origin disagree with their accepted root."

                let! reminderRows =
                    namedRows
                        ($"SELECT {WorkStorage.reminderColumns} FROM claimcore.reminders r WHERE r.case_id=$1 AND r.reminder_id=ANY($2::uuid[])")
                        [|
                            box (CaseId.value id)
                            box (reminderIds |> List.map ReminderId.value |> List.toArray)
                        |]

                if reminderRows.Length <> facts.Reminders.Count then
                    corrupt "Selected reminder root/projection presence disagrees."

                for row in reminderRows do
                    let stored = WorkStorage.reminderRow row

                    let part =
                        parts
                        |> List.find (fun part ->
                            part.Kind = PartKind.Reminder
                            && part.PartId = ReminderId.value stored.ReminderId)

                    if
                        stored.Snapshot <> PartChange.reminderSnapshot part
                        || stored.Revision <> part.Revision
                        || stored.RecordedAt <> part.Stamp.RecordedAt
                        || stored.RecordedBy <> ProjectionRows.familyActor part.Stamp.Authority
                    then
                        corrupt "Selected reminder projection disagrees with its accepted root."

                let! noteRows =
                    namedRows
                        ("SELECT "
                         + NoteStorage.columns
                         + NoteStorage.source
                         + " WHERE n.case_id=$1 AND n.note_id=ANY($2::uuid[])")
                        [|
                            box (CaseId.value id)
                            box (noteIds |> List.map NoteId.value |> List.toArray)
                        |]

                if noteRows.Length <> facts.Notes.Count then
                    corrupt "Selected note root/projection presence disagrees."

                for row in noteRows do
                    let stored = NoteStorage.row row
                    let note = Note.id stored.Note

                    let part =
                        parts
                        |> List.find (fun part ->
                            part.Kind = PartKind.Note && part.PartId = NoteId.value note)

                    let origin = Map.find (PartKind.Note, NoteId.value note) origins

                    if
                        Some stored.Note <> PartChange.noteSnapshot part
                        || stored.CreatedPosition <> origin.Position
                        || stored.RecordedAt <> origin.RecordedAt
                        || stored.RecordedBy <> ProjectionRows.familyActor origin.Authority
                    then
                        corrupt "Selected note values/origin disagree with their accepted root."

                return
                    Some
                        {
                            Facts = facts
                            Parts = parts
                            Origins = origins
                            Shape =
                                {
                                    MaterializedRows = materialized
                                    RootReads = rootReads
                                    PrivateRootReads = privateRoots
                                    RoundTrips = roundTrips
                                }
                        }
        }

    let family tx id = readFamily emptySelection tx id

    let familyForPayments tx id payments =
        readFamily
            { emptySelection with
                Payments = payments
            }
            tx
            id

    let familyForReminders tx id reminders =
        readFamily
            { emptySelection with
                Reminders = reminders
            }
            tx
            id

    let familyForReceipt (tx: ReadTx) id (changes: PartChange list) =
        task {
            let selected kind =
                changes
                |> List.filter (fun change -> change.Kind = kind)
                |> List.map _.PartId
                |> List.distinct

            let reminders = selected PartKind.Reminder
            let notes = selected PartKind.Note
            let parties = selected PartKind.Party

            let selection =
                { emptySelection with
                    Reminders = reminders |> List.map (DatabaseSession.admitted ReminderId.create)
                    Notes = notes |> List.map (DatabaseSession.admitted NoteId.create)
                    Parties = parties |> List.map (DatabaseSession.admitted PartyId.create)
                }

            let! loaded = readFamilyRoots selection tx id

            return
                loaded
                |> Option.map (fun roots ->
                    let revisions =
                        roots.Parts
                        |> List.map (fun part -> (part.Kind, part.PartId), part.Revision)
                        |> Map.ofList

                    for kind, ids in
                        [
                            PartKind.Reminder, reminders
                            PartKind.Note, notes
                            PartKind.Party, parties
                        ] do
                        if
                            ids
                            |> List.exists (fun part ->
                                not (Map.containsKey (kind, part) revisions))
                        then
                            corrupt "A receipt's selected accepted part revision is absent."

                    let snapshot =
                        roots.Parts
                        |> List.tryFind (fun part ->
                            part.Kind = PartKind.Case && part.PartId = CaseId.value id)
                        |> Option.bind (PartChange.caseSnapshot roots.Reference)
                        |> Option.defaultWith (fun () ->
                            corrupt "A receipt's current case root is absent.")

                    if snapshot <> roots.Projected.Snapshot then
                        corrupt "Receipt visibility disagrees with its accepted case root."

                    {
                        Snapshot = snapshot
                        Revisions = revisions
                    })
        }

    let familyForDecision tx id binding operation =
        let selected =
            match binding with
            | Binding.CasePayment(_, _, payment) ->
                { emptySelection with
                    Payments = [ payment ]
                }
            | Binding.Reminder(_, reminder, _) ->
                { emptySelection with
                    Reminders = [ reminder ]
                }
            | Binding.Note(_, note) -> { emptySelection with Notes = [ note ] }
            | Binding.Attachment(_, attachment) ->
                { emptySelection with
                    Attachments = [ attachment ]
                }
            | Binding.Party(_, party, _) ->
                { emptySelection with
                    Parties = [ party ]
                }
            | Binding.LinkNew _
            | Binding.Link _ -> { emptySelection with Links = true }
            | _ -> emptySelection

        readFamily { selected with Operation = operation } tx id

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

                let! latest, cache =
                    operation
                        tx
                        Map.empty
                        (DatabaseSession.value<Guid> 0 anchor
                         |> DatabaseSession.admitted OperationId.create)

                let! origin, cache =
                    operation
                        tx
                        cache
                        (DatabaseSession.value<Guid> 2 anchor
                         |> DatabaseSession.admitted OperationId.create)

                let! identity, _ =
                    operation
                        tx
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
