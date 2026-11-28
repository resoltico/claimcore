namespace ClaimCore.Application.Storage

open System
open System.Buffers.Binary
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.RecordFormat
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution

/// Acquires the accepted case-family roots needed by a decision or receipt.
module internal CaseFamilyAcquisition =
    let private corrupt message = raise (RecordCorruptException message)

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
                    "SELECT DISTINCT ON(e.part_kind,e.part_id) r.operation_id,e.effect_index,o.operation_id,f.effect_index,e.part_kind,e.part_id,e.revision,(SELECT max(s.operation_sequence) FROM claimcore.ledger_effects s WHERE s.aggregate_kind=$2 AND s.aggregate_id=$1) FROM claimcore.ledger_effects e JOIN claimcore.ledger r ON r.sequence=e.operation_sequence LEFT JOIN claimcore.ledger_effects f ON f.part_kind=e.part_kind AND f.part_id=e.part_id AND f.revision=1 LEFT JOIN claimcore.ledger o ON o.sequence=f.operation_sequence WHERE e.aggregate_kind=$2 AND e.aggregate_id=$1 AND (e.part_kind=ANY($3::text[]) OR (e.part_kind=$7 AND e.part_id=ANY($4::uuid[])) OR (e.part_kind=$8 AND e.part_id=ANY($5::uuid[])) OR (e.part_kind=$9 AND e.part_id=ANY($6::uuid[])) OR (e.part_kind=$11 AND (e.part_id=ANY($10::uuid[]) OR EXISTS(SELECT 1 FROM claimcore.case_parties p WHERE p.case_id=$1 AND p.party_id=e.part_id AND NOT p.removed AND p.role IS NOT NULL))) OR ($13 AND e.part_kind=$12 AND NOT EXISTS(SELECT 1 FROM claimcore.ledger_effects later WHERE later.part_kind=e.part_kind AND later.part_id=e.part_id AND later.revision>e.revision) AND e.retained->>'removed'='false' AND EXISTS(SELECT 1 FROM claimcore.ledger_link_dependencies d WHERE (d.operation_sequence,d.effect_index)=(e.operation_sequence,e.effect_index))) OR (e.part_kind=$14 AND e.part_id=ANY($15::uuid[]))) ORDER BY e.part_kind,e.part_id,e.revision DESC"
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
                let mutable cache = Map.empty

                let load includePrivate operation =
                    task {
                        let! value, updatedCache, loaded =
                            OperationStorage.acquire
                                tx
                                includePrivate
                                "Selected accepted root is absent."
                                cache
                                operation

                        cache <- updatedCache

                        if loaded then
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


    let private erasureScope (tx: ReadTx) caseId =
        task {
            let! rows =
                DatabaseSession.rows
                    tx.Connection
                    """SELECT d.operation_sequence,d.ordinal,latest.operation_sequence,latest.effect_index,latest.revision
                       FROM claimcore.erasure_relationships($1) d
                       JOIN claimcore.ledger_effects origin
                         ON (origin.operation_sequence,origin.effect_index)=(d.operation_sequence,d.effect_index)
                       CROSS JOIN LATERAL (
                           SELECT e.operation_sequence,e.effect_index,e.revision
                           FROM claimcore.ledger_effects e
                           WHERE e.part_kind=origin.part_kind AND e.part_id=origin.part_id
                           ORDER BY e.revision DESC LIMIT 1
                       ) latest
                       ORDER BY d.operation_sequence,d.ordinal"""
                    [| box (CaseId.value caseId) |]
                    tx.CancellationToken

            use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            let frame = Array.zeroCreate<byte> 32

            for row in rows do
                BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(0, 8), unbox row[0])
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(8, 4), unbox row[1])
                BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(12, 8), unbox row[2])
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(20, 4), unbox row[3])
                BinaryPrimitives.WriteInt64BigEndian(frame.AsSpan(24, 8), unbox row[4])
                hash.AppendData frame

            return hash.GetHashAndReset()
        }

    let issueErasureReview (tx: ReadTx) caseId requester sequence =
        task {
            let! scope = erasureScope tx caseId
            let plain = Array.zeroCreate<byte> 40
            BinaryPrimitives.WriteInt64BigEndian(plain.AsSpan(0, 8), Sequence.value sequence)
            scope.CopyTo(plain, 8)

            return
                tx.Cipher.SealCursor(
                    "case-erasure-review/" + CaseId.format caseId,
                    ActorId.value requester,
                    plain
                )
        }

    let admitErasureReview (tx: ReadTx) caseId requester token =
        task {
            match
                tx.Cipher.OpenCursor(
                    "case-erasure-review/" + CaseId.format caseId,
                    ActorId.value requester,
                    token
                )
            with
            | Some plain when plain.Length = 40 ->
                let! observed = erasureScope tx caseId

                if CryptographicOperations.FixedTimeEquals(observed, plain.AsSpan(8)) then
                    return
                        BinaryPrimitives.ReadInt64BigEndian(plain.AsSpan(0, 8)) |> Sequence.create
                else
                    return None
            | _ -> return None
        }
