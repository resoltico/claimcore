namespace ClaimCore.Application.Storage

open System
open ClaimCore.Domain
open ClaimCore.Application.Execution
open ClaimCore.RecordFormat

/// The sole SQL writer for aggregate-derived projection rows.
module internal PartProjections =
    let private caseParameters id (snapshot: CaseSnapshot) (index: CaseIndex) =
        [|
            box (CaseId.value id)
            box (CaseReference.format snapshot.Reference)
            box (Revision.value snapshot.Revision)
            box (CaseState.token snapshot.State)
            DatabaseSession.parameter (
                if snapshot.State = CaseState.Erased then
                    None
                else
                    Some(CaseRecord.encodeSnapshot snapshot)
            )
            DatabaseSession.parameter (index.Status |> Option.map CaseStatus.token)
            DatabaseSession.parameter (index.Progress |> Option.map CaseProgress.token)
            box index.ChangedAt
            DatabaseSession.parameter index.SearchText
            DatabaseSession.parameter (index.NotifiedOn |> Option.map CalendarDate.format)
            DatabaseSession.parameter (index.Claimed |> Option.map Money.value)
            DatabaseSession.parameter (index.Claimed |> Option.map Money.currency)
            DatabaseSession.parameter (index.Payable |> Option.map Money.value)
            DatabaseSession.parameter (index.Payable |> Option.map Money.currency)
            DatabaseSession.parameter (index.Paid |> Option.map Money.value)
            DatabaseSession.parameter (index.Responsible |> Option.map ActorId.value)
            DatabaseSession.parameter (index.FollowUp |> Option.map CalendarDate.format)
            DatabaseSession.parameter index.WorkSearchText
        |]

    let private exactlyOne count =
        if count <> 1 then
            raise (RecordCorruptException "Projection ownership or accepting origin changed.")

    let private optional = DatabaseSession.parameter

    let personIdentity connection (row: PersonProjection) token =
        task {
            let actor = row.Actor
            let kind, issuer, subject = PrincipalKey.parts actor.Principal

            let! _ = PrincipalLock.acquire connection actor.Principal token

            let! changed =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.actors(actor_id,principal_kind,issuer,subject,enabled,display_name,contact,display_name_search,contact_search,revision,principal_key) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) ON CONFLICT(actor_id) DO UPDATE SET enabled=EXCLUDED.enabled,display_name=EXCLUDED.display_name,contact=EXCLUDED.contact,display_name_search=EXCLUDED.display_name_search,contact_search=EXCLUDED.contact_search,revision=EXCLUDED.revision WHERE claimcore.actors.principal_key=EXCLUDED.principal_key AND claimcore.actors.principal_kind=EXCLUDED.principal_kind AND claimcore.actors.issuer=EXCLUDED.issuer AND claimcore.actors.subject=EXCLUDED.subject"
                    [|
                        box (ActorId.value actor.ActorId)
                        box (Vocabulary.token PrincipalKind.vocabulary kind)
                        box issuer
                        box subject
                        box actor.Enabled
                        optional (
                            actor.Profile
                            |> Option.map (fun profile -> PartyName.format profile.DisplayName)
                        )
                        optional (
                            actor.Profile |> Option.bind _.Contact |> Option.map ContactText.format
                        )
                        optional row.DisplayNameSearch
                        optional row.ContactSearch
                        box (Revision.value row.Revision)
                        box (PrincipalKey.naturalKey actor.Principal)
                    |]
                    token

            exactlyOne changed

            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.access_request WHERE principal_key=$1"
                    [| box (PrincipalKey.naturalKey actor.Principal) |]
                    token

            ()
        }

    let personGrants connection (row: PersonProjection) token =
        task {
            let actor = row.Actor

            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.grants WHERE actor_id=$1"
                    [| box (ActorId.value actor.ActorId) |]
                    token

            for KeyValue(grant, position) in row.Grants do
                let target =
                    match grant.Scope with
                    | Scope.Installation -> None
                    | Scope.Case id -> Some(CaseId.value id)

                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.grants(actor_id,role,case_id,granted_sequence,granted_effect_index) VALUES($1,$2,$3,$4,$5)"
                        [|
                            box (ActorId.value actor.ActorId)
                            box (Role.token grant.Role)
                            optional target
                            box (Sequence.value (EffectPosition.sequence position))
                            box (EffectPosition.index position)
                        |]
                        token

                ()

        }

    let person connection (row: PersonProjection) token =
        task {
            do! personIdentity connection row token
            do! personGrants connection row token
            let actor = row.Actor

            if row.ProfileErasure.IsSome then
                let! _ =
                    DatabaseSession.execute
                        connection
                        "SELECT claimcore.clear_owner('PROFILE',$1)"
                        [| box (ActorId.value actor.ActorId) |]
                        token

                ()
        }

    let family connection (row: FamilyProjection) token =
        task {
            let id = row.Snapshot.CaseId

            let! changed =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.cases(case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,notified_on,claimed_amount,claimed_currency,payable_amount,payable_currency,paid_amount,responsible_actor,follow_up,work_search_text) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::date,$11,$12,$13,$14,$15,$16,$17::date,$18) ON CONFLICT(case_id) DO UPDATE SET revision=EXCLUDED.revision,state=EXCLUDED.state,snapshot=EXCLUDED.snapshot,status=EXCLUDED.status,progress=EXCLUDED.progress,changed_at=EXCLUDED.changed_at,search_text=EXCLUDED.search_text,notified_on=EXCLUDED.notified_on,claimed_amount=EXCLUDED.claimed_amount,claimed_currency=EXCLUDED.claimed_currency,payable_amount=EXCLUDED.payable_amount,payable_currency=EXCLUDED.payable_currency,paid_amount=EXCLUDED.paid_amount,responsible_actor=EXCLUDED.responsible_actor,follow_up=EXCLUDED.follow_up,work_search_text=EXCLUDED.work_search_text WHERE claimcore.cases.case_reference=EXCLUDED.case_reference"
                    (caseParameters id row.Snapshot row.Index)
                    token

            exactlyOne changed

            for payment in row.Payments do
                let! changed =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_payments(case_id,payment_id,accepting_sequence,effect_index,payment_date,paid_amount,removed) VALUES($1,$2,$3,$4,$5::date,$6,$7) ON CONFLICT(payment_id) DO UPDATE SET payment_date=EXCLUDED.payment_date,paid_amount=EXCLUDED.paid_amount,removed=EXCLUDED.removed WHERE claimcore.case_payments.case_id=EXCLUDED.case_id AND claimcore.case_payments.accepting_sequence=EXCLUDED.accepting_sequence AND claimcore.case_payments.effect_index=EXCLUDED.effect_index"
                        [|
                            box (CaseId.value id)
                            box (OperationId.value payment.Id)
                            box (Sequence.value (EffectPosition.sequence payment.Origin.Position))
                            box (EffectPosition.index payment.Origin.Position)
                            optional (
                                payment.Value.Entry
                                |> Option.map (PaymentEntry.date >> CalendarDate.format)
                            )
                            optional (
                                payment.Value.Entry
                                |> Option.map (PaymentEntry.amount >> Amount.value)
                            )
                            box payment.Value.Removed
                        |]
                        token

                exactlyOne changed

            let work = row.Work

            let responsible =
                work.Snapshot |> Option.bind (fun snapshot -> snapshot.Work.Responsible)

            let summary = work.Snapshot |> Option.bind (fun snapshot -> snapshot.Work.Summary)

            let! _ =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.case_work(case_id,revision,snapshot,responsible_actor,handling_summary,search_text,changed_at,recorded_by,summary_changed_at,summary_recorded_by) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) ON CONFLICT(case_id) DO UPDATE SET revision=EXCLUDED.revision,snapshot=EXCLUDED.snapshot,responsible_actor=EXCLUDED.responsible_actor,handling_summary=EXCLUDED.handling_summary,search_text=EXCLUDED.search_text,changed_at=EXCLUDED.changed_at,recorded_by=EXCLUDED.recorded_by,summary_changed_at=EXCLUDED.summary_changed_at,summary_recorded_by=EXCLUDED.summary_recorded_by"
                    [|
                        box (CaseId.value id)
                        box (Revision.value work.Revision)
                        optional (work.Snapshot |> Option.map WorkRecord.encodeSnapshot)
                        optional (responsible |> Option.map ActorId.value)
                        optional (summary |> Option.map HandlingSummary.format)
                        optional (
                            summary |> Option.map (HandlingSummary.format >> TextSearch.normalize)
                        )
                        optional (work.Changed |> Option.map _.RecordedAt)
                        optional (work.Changed |> Option.map (_.RecordedBy >> ActorId.value))
                        optional (work.SummaryChanged |> Option.map _.RecordedAt)
                        optional (work.SummaryChanged |> Option.map (_.RecordedBy >> ActorId.value))
                    |]
                    token

            for reminder in row.Reminders do
                let snapshot =
                    reminder.Value.Plan
                    |> Option.map (fun plan ->
                        Reminder.ofAccepted
                            reminder.Id
                            id
                            reminder.Revision
                            plan.Action
                            plan.Due
                            reminder.Value.State
                        |> Result.defaultWith (fun _ ->
                            raise (RecordCorruptException "Reminder projection is invalid.")))

                let! changed =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.reminders(reminder_id,case_id,revision,snapshot,state,action,due,search_text,changed_at,recorded_by) VALUES($1,$2,$3,$4,$5,$6,$7::date,$8,$9,$10) ON CONFLICT(reminder_id) DO UPDATE SET revision=EXCLUDED.revision,snapshot=EXCLUDED.snapshot,state=EXCLUDED.state,action=EXCLUDED.action,due=EXCLUDED.due,search_text=EXCLUDED.search_text,changed_at=EXCLUDED.changed_at,recorded_by=EXCLUDED.recorded_by WHERE claimcore.reminders.case_id=EXCLUDED.case_id"
                        [|
                            box (ReminderId.value reminder.Id)
                            box (CaseId.value id)
                            box (Revision.value reminder.Revision)
                            optional (snapshot |> Option.map ReminderRecord.encodeSnapshot)
                            optional (
                                snapshot |> Option.map (Reminder.state >> ReminderState.token)
                            )
                            optional (
                                snapshot |> Option.map (Reminder.action >> ReminderAction.format)
                            )
                            optional (snapshot |> Option.map (Reminder.due >> CalendarDate.format))
                            optional (
                                snapshot
                                |> Option.map (
                                    Reminder.action >> ReminderAction.format >> TextSearch.normalize
                                )
                            )
                            box reminder.Changed.RecordedAt
                            box (ActorId.value reminder.Changed.RecordedBy)
                        |]
                        token

                exactlyOne changed

            for note in row.Notes do
                let! changed =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.notes(note_id,case_id,created_sequence,created_effect_index,text) VALUES($1,$2,$3,$4,$5) ON CONFLICT(note_id) DO UPDATE SET text=EXCLUDED.text WHERE claimcore.notes.case_id=EXCLUDED.case_id AND claimcore.notes.created_sequence=EXCLUDED.created_sequence AND claimcore.notes.created_effect_index=EXCLUDED.created_effect_index"
                        [|
                            box (NoteId.value note.Id)
                            box (CaseId.value id)
                            box (Sequence.value (EffectPosition.sequence note.Origin.Position))
                            box (EffectPosition.index note.Origin.Position)
                            optional (note.Value.Text |> Option.map NoteText.format)
                        |]
                        token

                exactlyOne changed

            if row.Snapshot.State = CaseState.Erased then
                // Erasure clears existing child projections without opening their private roots.
                for sql in
                    [
                        "UPDATE claimcore.case_payments SET payment_date=NULL,paid_amount=NULL WHERE case_id=$1"
                        "UPDATE claimcore.reminders SET snapshot=NULL,state=NULL,action=NULL,due=NULL,search_text=NULL WHERE case_id=$1"
                        "UPDATE claimcore.notes SET text=NULL WHERE case_id=$1"
                    ] do
                    let! _ =
                        DatabaseSession.execute connection sql [| box (CaseId.value id) |] token

                    ()

                let! _ =
                    DatabaseSession.execute
                        connection
                        "SELECT claimcore.clear_owner('FAMILY',$1)"
                        [| box (CaseId.value id) |]
                        token

                ()
        }

    let summary connection id token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT summary_changed_at,summary_recorded_by FROM claimcore.case_work WHERE case_id=$1"
                    [| box (CaseId.value id) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] ->
                match
                    DatabaseSession.optional<DateTime> 0 row, DatabaseSession.optional<Guid> 1 row
                with
                | None, None -> return None
                | Some at, Some by ->
                    return
                        Some
                            {
                                Attribution.RecordedAt = DateTimeOffset at
                                RecordedBy = DatabaseSession.admitted ActorId.create by
                            }
                | _ -> return raise (RecordCorruptException "Summary attribution is incomplete.")
            | _ -> return raise (RecordCorruptException "Work projection is duplicated.")
        }
