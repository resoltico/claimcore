namespace ClaimCore.Application.Storage

open System
open ClaimCore.Domain
open ClaimCore.Application.Execution
open ClaimCore.Application.Reads
open ClaimCore.RecordFormat

/// The sole SQL writer for aggregate-derived projection rows.
module internal PartProjections =
    let private caseParameters
        id
        (snapshot: CaseSnapshot)
        (index: CaseIndex)
        (dates: CaseReportDates)
        =
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
            DatabaseSession.parameter (index.ReceivedOn |> Option.map CalendarDate.format)
            DatabaseSession.parameter (index.Claimed |> Option.map Money.value)
            DatabaseSession.parameter (index.Claimed |> Option.map Money.currency)
            DatabaseSession.parameter (index.Payable |> Option.map Money.value)
            DatabaseSession.parameter (index.Payable |> Option.map Money.currency)
            DatabaseSession.parameter index.PaymentReview
            DatabaseSession.parameter (index.Responsible |> Option.map ActorId.value)
            DatabaseSession.parameter (index.FollowUp |> Option.map CalendarDate.format)
            DatabaseSession.parameter index.WorkSearchText
            DatabaseSession.parameter (index.Outcome |> Option.map ClosureOutcome.token)
            DatabaseSession.parameter (index.DeadlineDue |> Option.map CalendarDate.format)
            box (CalendarDate.format dates.Registered)
            DatabaseSession.parameter (dates.Closed |> Option.map CalendarDate.format)
            box (CalendarDate.format dates.LastActivity)
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
                    "INSERT INTO claimcore.actors(actor_id,principal_kind,issuer,subject,enabled,display_name,contact,display_name_search,contact_search,revision,principal_key) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11) ON CONFLICT(actor_id) DO UPDATE SET principal_key=EXCLUDED.principal_key,issuer=EXCLUDED.issuer,subject=EXCLUDED.subject,enabled=EXCLUDED.enabled,display_name=EXCLUDED.display_name,contact=EXCLUDED.contact,display_name_search=EXCLUDED.display_name_search,contact_search=EXCLUDED.contact_search,revision=EXCLUDED.revision WHERE claimcore.actors.principal_kind=EXCLUDED.principal_kind"
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

    let reportDateColumns =
        [ "registered_on"; "closed_on"; "last_activity_on" ]
        |> List.map (fun column -> DatabaseSession.dateExpression column + " AS " + column)
        |> String.concat ","

    let reportDates row =
        let date name =
            DatabaseSession.namedOptionalCalendarDate name row
            |> Option.map CalendarDate.ofDate

        match date "registered_on", date "last_activity_on" with
        | Some registered, Some lastActivity ->
            {
                CaseReportDates.Registered = registered
                Closed = date "closed_on"
                LastActivity = lastActivity
            }
        | _ -> raise (RecordCorruptException "Missing case reporting dates.")

    let previousReportDates connection id token =
        task {
            let! rows =
                DatabaseSession.namedRows
                    connection
                    ($"SELECT {reportDateColumns} FROM claimcore.cases WHERE case_id=$1")
                    [| box (CaseId.value id) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] -> return Some(reportDates row)
            | _ ->
                return raise (RecordCorruptException "A case has multiple reporting projections.")
        }

    let family connection (row: FamilyProjectionDelta) token =
        task {
            let id = row.Snapshot.CaseId

            let! changed =
                DatabaseSession.execute
                    connection
                    "INSERT INTO claimcore.cases(case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,received_on,claimed_amount,claimed_currency,payable_amount,payable_currency,payment_review,responsible_actor,follow_up,work_search_text,outcome,deadline_due,registered_on,closed_on,last_activity_on) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::date,$11,$12,$13,$14,$15,$16,$17::date,$18,$19,$20::date,$21::date,$22::date,$23::date) ON CONFLICT(case_id) DO UPDATE SET case_reference=EXCLUDED.case_reference,revision=EXCLUDED.revision,state=EXCLUDED.state,snapshot=EXCLUDED.snapshot,status=EXCLUDED.status,progress=EXCLUDED.progress,changed_at=EXCLUDED.changed_at,search_text=EXCLUDED.search_text,received_on=EXCLUDED.received_on,claimed_amount=EXCLUDED.claimed_amount,claimed_currency=EXCLUDED.claimed_currency,payable_amount=EXCLUDED.payable_amount,payable_currency=EXCLUDED.payable_currency,payment_review=EXCLUDED.payment_review,responsible_actor=EXCLUDED.responsible_actor,follow_up=EXCLUDED.follow_up,work_search_text=EXCLUDED.work_search_text,outcome=EXCLUDED.outcome,deadline_due,registered_on=EXCLUDED.registered_on,closed_on=EXCLUDED.closed_on,last_activity_on=EXCLUDED.last_activity_on"
                    (caseParameters id row.Snapshot row.Index row.ReportDates)
                    token

            exactlyOne changed

            for payment in row.Payments do
                let content =
                    match payment.Value.Entry with
                    | None -> PaymentProjectionContent.Cleared payment.Value.Removed
                    | Some entry -> PaymentProjectionContent.Available(entry, payment.Value.Removed)

                do!
                    PaymentStorage.restore
                        connection
                        {
                            CaseId = id
                            PaymentId = payment.Id
                            Position = payment.Origin.Position
                            Content = content
                        }
                        token

            let totals =
                CaseState.fields row.Snapshot.State
                |> Option.map (fun facts -> facts.Finance.CurrencyTotals)
                |> Option.defaultValue Map.empty

            do! PaymentStorage.updateTotals connection id totals token

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
                let snapshot = Reminder.ofValue reminder.Id id reminder.Revision reminder.Value

                let! changed =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.reminders(reminder_id,case_id,revision,snapshot,state,action,due,search_text,changed_at,recorded_by,kind,done_on) VALUES($1,$2,$3,$4,$5,$6,$7::date,$8,$9,$10,$11,$12::date) ON CONFLICT(reminder_id) DO UPDATE SET revision=EXCLUDED.revision,snapshot=EXCLUDED.snapshot,state=EXCLUDED.state,action=EXCLUDED.action,due=EXCLUDED.due,search_text=EXCLUDED.search_text,changed_at=EXCLUDED.changed_at,recorded_by=EXCLUDED.recorded_by,kind=EXCLUDED.kind,done_on=EXCLUDED.done_on WHERE claimcore.reminders.case_id=EXCLUDED.case_id"
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
                            optional (snapshot |> Option.map (Reminder.kind >> ReminderKind.token))
                            optional (
                                snapshot
                                |> Option.bind Reminder.doneOn
                                |> Option.map CalendarDate.format
                            )
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

            for party in row.Parties do
                let content = party.Value.Content

                let! changed =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_parties(party_id,case_id,revision,created_sequence,created_effect_index,removed,role,name,details) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT(party_id) DO UPDATE SET revision=EXCLUDED.revision,removed=EXCLUDED.removed,role=EXCLUDED.role,name=EXCLUDED.name,details=EXCLUDED.details WHERE claimcore.case_parties.case_id=EXCLUDED.case_id AND claimcore.case_parties.created_sequence=EXCLUDED.created_sequence AND claimcore.case_parties.created_effect_index=EXCLUDED.created_effect_index"
                        [|
                            box (PartyId.value party.Id)
                            box (CaseId.value id)
                            box (Revision.value party.Revision)
                            box (Sequence.value (EffectPosition.sequence party.Origin.Position))
                            box (EffectPosition.index party.Origin.Position)
                            box party.Value.Removed
                            optional (content |> Option.map (_.Role >> PartyRole.token))
                            optional (content |> Option.bind _.Name |> Option.map PartyName.format)
                            optional (
                                content |> Option.bind _.Details |> Option.map PartyDetails.format
                            )
                        |]
                        token

                exactlyOne changed

            for link in row.Links do
                let! changed =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_links(link_id,source_case_id,target_case_id,revision,removed,note) VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT(link_id) DO UPDATE SET target_case_id=EXCLUDED.target_case_id,revision=EXCLUDED.revision,removed=EXCLUDED.removed,note=EXCLUDED.note WHERE claimcore.case_links.source_case_id=EXCLUDED.source_case_id"
                        [|
                            box (LinkId.value link.Id)
                            box (CaseId.value link.Source)
                            optional (link.Value.Content |> Option.map (_.Target >> CaseId.value))
                            box (Revision.value link.Revision)
                            box link.Value.Removed
                            optional (
                                link.Value.Content
                                |> Option.bind _.Note
                                |> Option.map NoteText.format
                            )
                        |]
                        token

                exactlyOne changed

            if row.Snapshot.State = CaseState.Erased then
                // Erasure clears existing child projections without opening their private roots.
                for sql in
                    [
                        "UPDATE claimcore.case_payments SET payment_date=NULL,paid_amount=NULL,currency=NULL,direction=NULL,reference=NULL,payee_id=NULL WHERE case_id=$1"
                        "UPDATE claimcore.reminders SET snapshot=NULL,state=NULL,action=NULL,due=NULL,kind=NULL,done_on=NULL,search_text=NULL WHERE case_id=$1"
                        "UPDATE claimcore.notes SET text=NULL WHERE case_id=$1"
                        "UPDATE claimcore.case_parties SET role=NULL,name=NULL,details=NULL WHERE case_id=$1"
                        "UPDATE claimcore.case_links SET target_case_id=NULL,note=NULL WHERE source_case_id=$1"
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
