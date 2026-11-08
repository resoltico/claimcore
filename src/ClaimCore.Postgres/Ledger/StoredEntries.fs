namespace ClaimCore.Postgres


open System
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat

type internal LedgerRow =
    {
        Entry: StoredEntry
        Metadata: LedgerMetadata
    }

module internal StoredEntries =
    let position sequence bytes =
        let sequence = DatabaseSession.admitted Sequence.create sequence

        EntryHash.ofBytes bytes
        |> Option.map (LedgerPosition.create sequence)
        |> Option.defaultWith (fun () ->
            raise (RecordCorruptException "Invalid stored ledger position."))

    let columns =
        "sequence,previous_hash,entry_hash,installation_format,subject_kind,act,operation_id,actor_id,"
        + DatabaseSession.instantExpression "recorded_at"
        + ",subject_id,case_revision,"
        + DatabaseSession.dateExpression "business_date"
        + ",payload_commitment,payload,payload_nonce,creation_reference,parent_case_id,next_reminder_id,effect_index,effect_count,authority_operation"

    let private subjects row =
        let target = DatabaseSession.value<Guid> 9 row
        let act = DatabaseSession.value<string> 5 row

        match DatabaseSession.value<string> 4 row with
        | "AUTHORITY" ->
            let target = DatabaseSession.admitted ActorId.create target

            if not (Convert.IsDBNull row[10]) || not (Convert.IsDBNull row[11]) then
                raise (RecordCorruptException "Invalid authority ledger shape.")

            let kind =
                (Vocabulary.tryParse AuthorityKind.vocabulary) act
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid authority act."))

            EntryTarget.Authority(target, kind),
            RecordedTarget.Authority { TargetActorId = target; Act = kind }
        | "CASE" ->
            let target = DatabaseSession.admitted CaseId.create target

            let revision =
                DatabaseSession.value<int64> 10 row |> DatabaseSession.admitted Revision.create

            let kind =
                (Vocabulary.tryParse CaseActKind.vocabulary) act
                |> Option.defaultWith (fun () -> raise (RecordCorruptException "Invalid case act."))

            let date = DatabaseSession.calendarDate 11 row

            EntryTarget.Case(target, revision, kind, date),
            RecordedTarget.Case
                {
                    CaseId = target
                    Revision = revision
                    Act = kind
                    BusinessDate = date
                }
        | "WORK" ->
            let id = DatabaseSession.admitted CaseId.create target

            let parent =
                DatabaseSession.value<Guid> 16 row |> DatabaseSession.admitted CaseId.create

            if id <> parent || act <> "UPDATE_WORK" || not (Convert.IsDBNull row[17]) then
                raise (RecordCorruptException "Invalid work target.")

            let subject =
                {
                    WorkEntryMetadata.CaseId = id
                    Revision =
                        DatabaseSession.value<int64> 10 row
                        |> DatabaseSession.admitted Revision.create
                    Act = WorkActKind.UpdateWork
                    BusinessDate = DatabaseSession.calendarDate 11 row
                }

            EntryTarget.Work subject, RecordedTarget.Work subject
        | "NOTE" ->
            if not (Convert.IsDBNull row[10]) || not (Convert.IsDBNull row[17]) then
                raise (RecordCorruptException "Invalid note target.")

            let subject =
                {
                    NoteEntryMetadata.CaseId =
                        DatabaseSession.value<Guid> 16 row |> DatabaseSession.admitted CaseId.create
                    NoteId = DatabaseSession.admitted NoteId.create target
                    Act =
                        Vocabulary.tryParse NoteActKind.vocabulary act
                        |> Option.defaultWith (fun () ->
                            raise (RecordCorruptException "Invalid note act."))
                    BusinessDate = DatabaseSession.calendarDate 11 row
                }

            EntryTarget.Note subject, RecordedTarget.Note subject
        | "REMINDER" ->
            let kind =
                Vocabulary.tryParse ReminderActKind.vocabulary act
                |> Option.defaultWith (fun () ->
                    raise (RecordCorruptException "Invalid reminder act."))

            let subject =
                {
                    ReminderEntryMetadata.CaseId =
                        DatabaseSession.value<Guid> 16 row |> DatabaseSession.admitted CaseId.create
                    ReminderId = DatabaseSession.admitted ReminderId.create target
                    Revision =
                        DatabaseSession.value<int64> 10 row
                        |> DatabaseSession.admitted Revision.create
                    Act = kind
                    NextReminderId =
                        DatabaseSession.optional<Guid> 17 row
                        |> Option.map (DatabaseSession.admitted ReminderId.create)
                    BusinessDate = DatabaseSession.calendarDate 11 row
                }

            EntryTarget.Reminder subject, RecordedTarget.Reminder subject
        | _ -> raise (RecordCorruptException "Invalid ledger subject.")

    let decode installationId row =
        if DatabaseSession.value<int16> 3 row <> int16 InstallationFormat.Current then
            raise (RecordCorruptException "Unsupported ledger format.")

        let subject, metadataSubject = subjects row

        let effects =
            let operation =
                DatabaseSession.optional<string> 20 row
                |> Option.map (fun value ->
                    (Vocabulary.tryParse AuthorityKind.vocabulary) value
                    |> Option.defaultWith (fun () ->
                        raise (RecordCorruptException "Invalid authority operation.")))

            LedgerEffects.create
                operation
                (DatabaseSession.value<int> 19 row)
                (DatabaseSession.value<int> 18 row)
            |> Option.defaultWith (fun () ->
                raise (RecordCorruptException "Invalid operation effect group."))

        let entry: StoredEntry =
            {
                Effects = effects
                Sequence = DatabaseSession.value 0 row |> DatabaseSession.admitted Sequence.create
                EntryHash = DatabaseSession.value 2 row
                OperationId =
                    DatabaseSession.value 6 row |> DatabaseSession.admitted OperationId.create
                ActorId = DatabaseSession.value 7 row |> DatabaseSession.admitted ActorId.create
                RecordedAt = DatabaseSession.recordedInstant 8 row
                Subject = subject
                CreationReference =
                    DatabaseSession.optional<string> 15 row
                    |> Option.map (
                        CaseReference.parse
                        >> Result.defaultWith (fun _ ->
                            raise (RecordCorruptException "Invalid retained reference."))
                    )
                PayloadCommitment = DatabaseSession.value 12 row
                Payload = DatabaseSession.optional<byte array> 13 row
                PayloadNonce = DatabaseSession.optional<byte array> 14 row
            }

        {
            Entry = entry
            Metadata =
                {
                    Effects = entry.Effects
                    InstallationId = installationId
                    Sequence = entry.Sequence
                    PreviousHash = DatabaseSession.value 1 row
                    OperationId = entry.OperationId
                    ActorId = entry.ActorId
                    RecordedAt = entry.RecordedAt
                    Subject = metadataSubject
                    CreationReference = entry.CreationReference
                    PayloadCommitment = entry.PayloadCommitment
                }
        }

    let operation connection installationId id token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {columns} FROM claimcore.ledger WHERE operation_id=$1 ORDER BY effect_index LIMIT {LedgerEffects.MaximumCount + 1}")
                    [| box (OperationId.value id) |]
                    token

            match rows with
            | [] -> return None
            | rows ->
                let entries = rows |> List.map (decode installationId >> _.Entry)

                return
                    StoredOperation.create entries
                    |> Result.map Some
                    |> Result.defaultWith (fun _ ->
                        raise (
                            RecordCorruptException
                                "Operation effect group is incomplete or inconsistent."
                        ))
        }
