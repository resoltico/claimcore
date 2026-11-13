namespace ClaimCore.Domain

/// Related entities keep their own identities and attribution when the case's private content is cleared.
type CaseFamilyState =
    {
        Work: WorkSubjectState option
        Reminders: Map<ReminderId, ReminderSubjectState>
        Notes: Map<NoteId, NoteSubjectState>
    }

type CaseFamilyRows =
    {
        Work: WorkSubjectRows option
        Reminders: ReminderSubjectRows list
        Notes: NoteSubjectRows list
    }

module CaseFamily =
    let onCaseCreated id : CaseFamilyState =
        {
            Work = Some(WorkSubject.initial id)
            Reminders = Map.empty
            Notes = Map.empty
        }

    let validate caseId (family: CaseFamilyState) =
        let owned =
            (family.Work |> Option.exists (fun work -> work.CaseId = caseId))
            && (family.Reminders
                |> Map.forall (fun id reminder ->
                    id = reminder.ReminderId && reminder.CaseId = caseId))
            && (family.Notes
                |> Map.forall (fun id note -> id = note.NoteId && note.CaseId = caseId))

        if owned then
            Ok()
        else
            Error RecordViolation.InvalidSubjectState

    let onCaseErased (parent: CaseSubjectState) (family: CaseFamilyState) =
        if parent.Lifecycle <> Lifecycle.Erased then
            Error RecordViolation.InvalidSubjectState
        else
            validate parent.CaseId family
            |> Result.map (fun () ->
                {
                    CaseFamilyState.Work = family.Work |> Option.map WorkSubject.erase
                    Reminders =
                        family.Reminders |> Map.map (fun _ state -> ReminderSubject.erase state)
                    Notes = family.Notes |> Map.map (fun _ state -> NoteSubject.erase state)
                })

    let rows (family: CaseFamilyState) =
        let work = family.Work |> Option.map WorkSubject.rows

        let reminders =
            family.Reminders |> Map.values |> Seq.map ReminderSubject.rows |> Seq.toList

        let notes = family.Notes |> Map.values |> Seq.map NoteSubject.rows |> Seq.toList

        if
            (work |> Option.exists Option.isNone)
            || List.exists Option.isNone reminders
            || List.exists Option.isNone notes
        then
            Error RecordViolation.InvalidSubjectState
        else
            Ok
                {
                    CaseFamilyRows.Work = work |> Option.flatten
                    Reminders = reminders |> List.choose id
                    Notes = notes |> List.choose id
                }
