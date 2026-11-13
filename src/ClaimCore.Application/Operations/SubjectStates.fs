namespace ClaimCore.Application

open ClaimCore.Domain

/// Store records adapt to subject-owned state without re-deciding any business fact.
module internal SubjectStates =
    let work (parent: CaseSubjectState) (record: StoredWork) : WorkSubjectState =
        {
            CaseId = record.CaseId
            Revision = record.Revision
            Snapshot = record.Snapshot
            ChangedAt = record.ChangedAt
            RecordedBy = record.RecordedBy
            SummaryChangedAt = record.SummaryChangedAt
            SummaryRecordedBy = record.SummaryRecordedBy
            Erased = parent.Lifecycle = Lifecycle.Erased
        }

    let reminder (parent: CaseSubjectState) (record: StoredReminder) : ReminderSubjectState =
        {
            ReminderId = record.ReminderId
            CaseId = record.CaseId
            Revision = record.Revision
            Status = record.Snapshot |> Option.map Reminder.state
            Snapshot = record.Snapshot
            RecordedAt = record.RecordedAt
            RecordedBy = record.RecordedBy
            Erased = parent.Lifecycle = Lifecycle.Erased
        }

    let note (parent: CaseSubjectState) (record: StoredNote) : NoteSubjectState =
        {
            NoteId = Note.id record.Note
            CaseId = Note.caseId record.Note
            Removed =
                if parent.Lifecycle = Lifecycle.Erased then
                    None
                else
                    Some(Note.text record.Note |> Option.isNone)
            Snapshot = Some record.Note
            Created =
                {
                    Position = record.CreatedPosition
                    OperationId =
                        OperationId.create (NoteId.value (Note.id record.Note)) |> Option.get
                    ActorId = record.RecordedBy
                    RecordedAt = record.RecordedAt
                }
            Erased = parent.Lifecycle = Lifecycle.Erased
        }
