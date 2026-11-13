namespace ClaimCore.Postgres


open System
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal Persistence =
    let private caseParameters id (snapshot: CaseSnapshot) (index: CaseIndex) =
        [|
            box (CaseId.value id)
            box (CaseReference.format snapshot.Reference)
            box (Revision.value snapshot.Revision)
            box (CaseState.token snapshot.State)
            box (CaseRecord.encodeSnapshot snapshot)
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
            DatabaseSession.parameter (index.FollowUp |> Option.map CalendarDate.value)
            DatabaseSession.parameter index.WorkSearchText
        |]

    let case connection projection token =
        task {
            match projection with
            | CaseProjection.Insert(id, snapshot, index) ->
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.cases (case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,notified_on,claimed_amount,claimed_currency,payable_amount,payable_currency,paid_amount,responsible_actor,follow_up,work_search_text) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::date,$11,$12,$13,$14,$15,$16,$17,$18)"
                        (caseParameters id snapshot index)
                        token

                let root = (WorkSubject.initial id).Snapshot.Value

                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_work(case_id,revision,snapshot) VALUES($1,0,$2)"
                        [| box (CaseId.value id); box (WorkRecord.encodeSnapshot root) |]
                        token

                return ()
            | CaseProjection.Update(id, snapshot, index, payment) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.cases SET case_reference=$2,revision=$3,state=$4,snapshot=$5,status=$6,progress=$7,changed_at=$8,search_text=$9,notified_on=$10::date,claimed_amount=$11,claimed_currency=$12,payable_amount=$13,payable_currency=$14,paid_amount=$15,responsible_actor=$16,follow_up=$17,work_search_text=$18 WHERE case_id=$1"
                        (caseParameters id snapshot index)
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Case projection is absent.")

                do! PaymentStorage.apply connection id payment token
            | CaseProjection.Erase(id, revision, changedAt) ->
                do! PaymentStorage.erase connection id token

                let! _ =
                    DatabaseSession.rows
                        connection
                        "SELECT claimcore.erase_case_payload($1)"
                        [| box (CaseId.value id) |]
                        token

                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.cases SET revision=$2,state='ERASED',snapshot=NULL,status=NULL,progress=NULL,search_text=NULL,notified_on=NULL,claimed_amount=NULL,claimed_currency=NULL,payable_amount=NULL,payable_currency=NULL,paid_amount=NULL,responsible_actor=NULL,follow_up=NULL,work_search_text=NULL,changed_at=$3 WHERE case_id=$1"
                        [| box (CaseId.value id); box (Revision.value revision); box changedAt |]
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Case projection is absent.")
        }

    let caseIndex connection id (index: CaseIndex) token =
        task {
            let! changed =
                DatabaseSession.execute
                    connection
                    "UPDATE claimcore.cases SET responsible_actor=$2,follow_up=$3,work_search_text=$4,status=$5,progress=$6,changed_at=$7,search_text=$8,notified_on=$9::date,claimed_amount=$10,claimed_currency=$11,payable_amount=$12,payable_currency=$13,paid_amount=$14 WHERE case_id=$1"
                    [|
                        box (CaseId.value id)
                        DatabaseSession.parameter (index.Responsible |> Option.map ActorId.value)
                        DatabaseSession.parameter (index.FollowUp |> Option.map CalendarDate.value)
                        DatabaseSession.parameter index.WorkSearchText
                        DatabaseSession.parameter (index.Status |> Option.map CaseStatus.token)
                        DatabaseSession.parameter (index.Progress |> Option.map CaseProgress.token)
                        box index.ChangedAt
                        DatabaseSession.parameter index.SearchText
                        DatabaseSession.parameter (
                            index.NotifiedOn |> Option.map CalendarDate.value
                        )
                        DatabaseSession.parameter (index.Claimed |> Option.map Money.value)
                        DatabaseSession.parameter (index.Claimed |> Option.map Money.currency)
                        DatabaseSession.parameter (index.Payable |> Option.map Money.value)
                        DatabaseSession.parameter (index.Payable |> Option.map Money.currency)
                        DatabaseSession.parameter (index.Paid |> Option.map Money.value)
                    |]
                    token

            if changed <> 1 then
                raise (RecordCorruptException "Family index target is absent.")
        }

    let peopleActor connection (row: PeopleSubjectRows) token =
        task {
            let actor = row.Actor
            let! _ = AccessRequests.lockPrincipal connection actor.Principal token
            let identity = AccessRequests.parameters actor.Principal

            let! existing =
                DatabaseSession.rows
                    connection
                    "SELECT principal_kind,issuer,subject FROM claimcore.actors WHERE actor_id=$1"
                    [| box (ActorId.value actor.ActorId) |]
                    token

            let creating = existing.IsEmpty

            match existing with
            | [] -> ()
            | [ stored ] when
                [| for index in 0..2 -> box (DatabaseSession.value<string> index stored) |] =
                    identity
                ->
                ()
            | _ ->
                raise (RecordCorruptException "People row cannot change its registered principal.")

            let parameters =
                Array.concat
                    [
                        [| box (ActorId.value actor.ActorId) |]
                        identity
                        [|
                            box actor.Enabled
                            DatabaseSession.parameter (
                                actor.Profile
                                |> Option.map (fun profile -> PartyName.format profile.DisplayName)
                            )
                            DatabaseSession.parameter (
                                actor.Profile
                                |> Option.bind _.Contact
                                |> Option.map ContactText.format
                            )
                            DatabaseSession.parameter row.DisplayNameSearch
                            DatabaseSession.parameter row.ContactSearch
                        |]
                    ]

            let sql =
                if creating then
                    "INSERT INTO claimcore.actors(actor_id,principal_kind,issuer,subject,enabled,display_name,contact,display_name_search,contact_search) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)"
                else
                    "UPDATE claimcore.actors SET enabled=$5,display_name=$6,contact=$7,display_name_search=$8,contact_search=$9 WHERE actor_id=$1 AND principal_kind=$2 AND issuer=$3 AND subject=$4"

            let! changed = DatabaseSession.execute connection sql parameters token

            if changed <> 1 then
                raise (RecordCorruptException "People row target is absent.")

            if creating then
                let! _ = AccessRequests.delete connection actor.Principal token
                ()

            match row.ProfileErasure with
            | None -> ()
            | Some position ->
                let! erased =
                    DatabaseSession.rows
                        connection
                        "SELECT claimcore.erase_profile_payload($1) WHERE EXISTS(SELECT 1 FROM claimcore.ledger_effects WHERE operation_sequence=$2 AND effect_index=$3 AND subject_kind='PEOPLE' AND subject_id=$1 AND act='people.eraseProfile')"
                        [|
                            box (ActorId.value actor.ActorId)
                            box (Sequence.value (EffectPosition.sequence position))
                            box (EffectPosition.index position)
                        |]
                        token

                if erased.Length <> 1 then
                    raise (RecordCorruptException "Profile erase has no matching accepted effect.")
        }

    let peopleGrants connection (row: PeopleSubjectRows) token =
        task {
            let actor = row.Actor

            let! _ =
                DatabaseSession.execute
                    connection
                    "DELETE FROM claimcore.grants WHERE actor_id=$1"
                    [| box (ActorId.value actor.ActorId) |]
                    token

            for KeyValue(grant, position) in row.Grants do
                let caseId =
                    match grant.Scope with
                    | Scope.Installation -> None
                    | Scope.Case id -> Some(CaseId.value id)

                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.grants(granted_sequence,granted_effect_index,actor_id,role,case_id) VALUES($1,$2,$3,$4,$5)"
                        [|
                            box (Sequence.value (EffectPosition.sequence position))
                            box (EffectPosition.index position)
                            box (ActorId.value actor.ActorId)
                            box (Role.token grant.Role)
                            DatabaseSession.parameter caseId
                        |]
                        token

                ()
        }

    let people connection row token =
        task {
            do! peopleActor connection row token
            do! peopleGrants connection row token
        }

    let workChange connection projection token =
        task {
            match projection with
            | WorkChangeProjection.Note row ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.notes(note_id,case_id,created_sequence,created_effect_index,text) SELECT $1,$2,operation_sequence,effect_index,$3 FROM claimcore.ledger_effects WHERE subject_kind='NOTE' AND subject_id=$1 AND act='note.add' AND operation_sequence=$4 AND effect_index=$5 ON CONFLICT(note_id) DO UPDATE SET text=EXCLUDED.text WHERE claimcore.notes.case_id=EXCLUDED.case_id AND claimcore.notes.created_sequence=EXCLUDED.created_sequence AND claimcore.notes.created_effect_index=EXCLUDED.created_effect_index"
                        [|
                            box (NoteId.value row.NoteId)
                            box (CaseId.value row.CaseId)
                            DatabaseSession.parameter (row.Text |> Option.map NoteText.format)
                            box (Sequence.value (EffectPosition.sequence row.CreatedPosition))
                            box (EffectPosition.index row.CreatedPosition)
                        |]
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Note creation source is absent or mismatched.")
            | WorkChangeProjection.Work projection ->
                let state = projection

                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_work(case_id,revision,snapshot,responsible_actor,handling_summary,search_text,changed_at,recorded_by,summary_changed_at,summary_recorded_by) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) ON CONFLICT(case_id) DO UPDATE SET revision=EXCLUDED.revision,snapshot=EXCLUDED.snapshot,responsible_actor=EXCLUDED.responsible_actor,handling_summary=EXCLUDED.handling_summary,search_text=EXCLUDED.search_text,changed_at=EXCLUDED.changed_at,recorded_by=EXCLUDED.recorded_by,summary_changed_at=EXCLUDED.summary_changed_at,summary_recorded_by=EXCLUDED.summary_recorded_by"
                        [|
                            box (CaseId.value state.CaseId)
                            box (Revision.value state.Revision)
                            DatabaseSession.parameter (
                                state.Snapshot |> Option.map WorkRecord.encodeSnapshot
                            )
                            DatabaseSession.parameter (
                                projection.Responsible |> Option.map ActorId.value
                            )
                            DatabaseSession.parameter (
                                projection.Summary |> Option.map HandlingSummary.format
                            )
                            DatabaseSession.parameter projection.SearchText
                            DatabaseSession.parameter state.ChangedAt
                            DatabaseSession.parameter (state.RecordedBy |> Option.map ActorId.value)
                            DatabaseSession.parameter state.SummaryChangedAt
                            DatabaseSession.parameter (
                                state.SummaryRecordedBy |> Option.map ActorId.value
                            )
                        |]
                        token

                return ()
            | WorkChangeProjection.Reminder rows ->
                for row in rows do
                    let state = row

                    let! changed =
                        DatabaseSession.execute
                            connection
                            "INSERT INTO claimcore.reminders(reminder_id,case_id,revision,snapshot,state,action,due,search_text,changed_at,recorded_by) VALUES($1,$2,$3,$4,$5,$6,$7::date,$8,$9,$10) ON CONFLICT(reminder_id) DO UPDATE SET revision=EXCLUDED.revision,snapshot=EXCLUDED.snapshot,state=EXCLUDED.state,action=EXCLUDED.action,due=EXCLUDED.due,search_text=EXCLUDED.search_text,changed_at=EXCLUDED.changed_at,recorded_by=EXCLUDED.recorded_by WHERE claimcore.reminders.case_id=EXCLUDED.case_id"
                            [|
                                box (ReminderId.value state.ReminderId)
                                box (CaseId.value state.CaseId)
                                box (Revision.value state.Revision)
                                DatabaseSession.parameter (
                                    state.Snapshot |> Option.map ReminderRecord.encodeSnapshot
                                )
                                DatabaseSession.parameter (
                                    row.Status |> Option.map ReminderState.token
                                )
                                DatabaseSession.parameter (
                                    row.Action |> Option.map ReminderAction.format
                                )
                                DatabaseSession.parameter (row.Due |> Option.map CalendarDate.value)
                                DatabaseSession.parameter row.SearchText
                                box state.RecordedAt
                                box (ActorId.value state.RecordedBy)
                            |]
                            token

                    if changed <> 1 then
                        raise (RecordCorruptException "Reminder projection has another parent.")
        }
