namespace ClaimCore.Postgres


open System
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal Persistence =
    type private SubjectColumns =
        {
            Kind: string
            Id: Guid
            Revision: Revision option
            Act: string
            Date: DateOnly option
            Parent: CaseId option
            Next: ReminderId option
        }

    let entry connection (entry: EntryDraft) token =
        let subject =
            match entry.Subject with
            | EntryTarget.NonAcceptance operation ->
                {
                    Kind = "CONTROL"
                    Id = OperationId.value operation
                    Revision = None
                    Act = "NOT_ACCEPTED"
                    Date = None
                    Parent = None
                    Next = None
                }
            | EntryTarget.Authority(target, kind) ->
                {
                    Kind = "AUTHORITY"
                    Id = ActorId.value target
                    Revision = None
                    Act = DurableNames.authorityAct kind
                    Date = None
                    Parent = None
                    Next = None
                }
            | EntryTarget.Case(id, revision, kind, date) ->
                {
                    Kind = "CASE"
                    Id = CaseId.value id
                    Revision = Some revision
                    Act = DurableNames.caseAct kind
                    Date = Some date
                    Parent = None
                    Next = None
                }
            | EntryTarget.Work subject ->
                {
                    Kind = "WORK"
                    Id = CaseId.value subject.CaseId
                    Revision = Some subject.Revision
                    Act = "UPDATE_WORK"
                    Date = Some subject.BusinessDate
                    Parent = Some subject.CaseId
                    Next = None
                }
            | EntryTarget.Reminder subject ->
                {
                    Kind = "REMINDER"
                    Id = ReminderId.value subject.ReminderId
                    Revision = Some subject.Revision
                    Act = ReminderActKind.token subject.Act
                    Date = Some subject.BusinessDate
                    Parent = Some subject.CaseId
                    Next = subject.NextReminderId
                }

        DatabaseSession.execute
            connection
            "INSERT INTO claimcore.ledger(sequence,previous_hash,entry_hash,entry_format,subject_kind,act,operation_id,actor_id,recorded_at,subject_id,case_revision,business_date,payload_commitment,payload,payload_nonce,creation_reference,parent_case_id,next_reminder_id) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18)"
            [|
                box (Sequence.value entry.Sequence)
                box entry.PreviousHash
                box entry.EntryHash
                box (int16 RecordVersions.LedgerEntry)
                box subject.Kind
                box subject.Act
                box (OperationId.value entry.OperationId)
                box (ActorId.value entry.ActorId)
                box entry.RecordedAt
                box subject.Id
                DatabaseSession.parameter (subject.Revision |> Option.map Revision.value)
                DatabaseSession.parameter subject.Date
                box entry.PayloadCommitment
                box entry.Payload
                box entry.PayloadNonce
                DatabaseSession.parameter (
                    entry.CreationReference |> Option.map CaseReference.format
                )
                DatabaseSession.parameter (subject.Parent |> Option.map CaseId.value)
                DatabaseSession.parameter (subject.Next |> Option.map ReminderId.value)
            |]
            token

    let private caseParameters id (snapshot: CaseSnapshot) (index: CaseIndex) =
        [|
            box (CaseId.value id)
            box (CaseReference.format snapshot.Reference)
            box (Revision.value snapshot.Revision)
            box (CaseState.token snapshot.State)
            box (CaseRecord.encodeSnapshot snapshot)
            DatabaseSession.parameter (index.Status |> Option.map DurableNames.status)
            DatabaseSession.parameter (index.Progress |> Option.map DurableNames.progress)
            box index.ChangedAt
            DatabaseSession.parameter index.SearchText
            DatabaseSession.parameter (index.NotifiedOn |> Option.map CalendarDate.value)
            DatabaseSession.parameter (index.Claimed |> Option.map Money.value)
            DatabaseSession.parameter (index.Claimed |> Option.map Money.currency)
            DatabaseSession.parameter (index.Payable |> Option.map Money.value)
            DatabaseSession.parameter (index.Payable |> Option.map Money.currency)
            DatabaseSession.parameter (index.Responsible |> Option.map ActorId.value)
            DatabaseSession.parameter (index.FollowUp |> Option.map CalendarDate.value)
        |]

    let case connection projection token =
        task {
            match projection with
            | CaseProjection.Insert(id, snapshot, index) ->
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.cases (case_id,case_reference,revision,state,snapshot,status,progress,changed_at,search_text,notified_on,claimed_amount,claimed_currency,payable_amount,payable_currency,responsible_actor,follow_up) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)"
                        (caseParameters id snapshot index)
                        token

                let root =
                    {
                        CaseWorkSnapshot.CaseId = id
                        Revision = Revision.zero
                        Work = CaseWork.empty
                    }

                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.case_work(case_id,revision,snapshot) VALUES($1,0,$2)"
                        [| box (CaseId.value id); box (WorkRecord.encodeSnapshot root) |]
                        token

                return ()
            | CaseProjection.Update(id, snapshot, index) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.cases SET case_reference=$2,revision=$3,state=$4,snapshot=$5,status=$6,progress=$7,changed_at=$8,search_text=$9,notified_on=$10,claimed_amount=$11,claimed_currency=$12,payable_amount=$13,payable_currency=$14 WHERE case_id=$1"
                        (caseParameters id snapshot index |> Array.take 14)
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Case projection is absent.")
            | CaseProjection.Erase(id, revision, changedAt) ->
                let! _ =
                    DatabaseSession.rows
                        connection
                        "SELECT claimcore.erase_case_payload($1)"
                        [| box (CaseId.value id) |]
                        token

                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.cases SET revision=$2,state='ERASED',snapshot=NULL,status=NULL,progress=NULL,search_text=NULL,notified_on=NULL,claimed_amount=NULL,claimed_currency=NULL,payable_amount=NULL,payable_currency=NULL,responsible_actor=NULL,follow_up=NULL,work_search_text=NULL,changed_at=$3 WHERE case_id=$1"
                        [| box (CaseId.value id); box (Revision.value revision); box changedAt |]
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Case projection is absent.")
        }

    let private register connection id principal token =
        let kind, issuer, subject = PrincipalKey.parts principal

        DatabaseSession.execute
            connection
            "INSERT INTO claimcore.actors VALUES ($1,$2,$3,$4,true)"
            [|
                box (ActorId.value id)
                box (DurableNames.principalKind kind)
                box issuer
                box subject
            |]
            token

    let authority connection projection token =
        task {
            match projection with
            | AuthorityProjection.Register(id, principal) ->
                let! _ = register connection id principal token
                return ()
            | AuthorityProjection.ProvisionOwner(id, principal) ->
                let! _ = register connection id principal token

                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.grants VALUES ($1,$2,NULL)"
                        [| box (ActorId.value id); box (DurableNames.role Role.Owner) |]
                        token

                return ()
            | AuthorityProjection.SetEnabled(id, enabled) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.actors SET enabled=$2 WHERE actor_id=$1"
                        [| box (ActorId.value id); box enabled |]
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Authority projection is absent.")
            | AuthorityProjection.AddGrant(id, role, caseId) ->
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.grants VALUES ($1,$2,$3) ON CONFLICT DO NOTHING"
                        [|
                            box (ActorId.value id)
                            box (DurableNames.role role)
                            DatabaseSession.parameter (caseId |> Option.map CaseId.value)
                        |]
                        token

                return ()
            | AuthorityProjection.RemoveGrant(id, role, caseId) ->
                let! _ =
                    DatabaseSession.execute
                        connection
                        "DELETE FROM claimcore.grants WHERE actor_id=$1 AND role=$2 AND case_id IS NOT DISTINCT FROM $3"
                        [|
                            box (ActorId.value id)
                            box (DurableNames.role role)
                            DatabaseSession.parameter (caseId |> Option.map CaseId.value)
                        |]
                        token

                return ()
        }


    let workChange connection projection token =
        let reminderValue changedAt recordedBy insert value =
            task {
                let parameters =
                    [|
                        box (ReminderId.value (Reminder.id value))
                        box (CaseId.value (Reminder.caseId value))
                        box (Revision.value (Reminder.revision value))
                        box (ReminderRecord.encodeSnapshot value)
                        box (ReminderState.token (Reminder.state value))
                        box (ReminderAction.format (Reminder.action value))
                        box (CalendarDate.format (Reminder.due value))
                        box (CaseSearch.normalize (ReminderAction.format (Reminder.action value)))
                        box changedAt
                        box (ActorId.value recordedBy)
                    |]

                let sql =
                    if insert then
                        "INSERT INTO claimcore.reminders(reminder_id,case_id,revision,snapshot,state,action,due,search_text,changed_at,recorded_by) VALUES($1,$2,$3,$4,$5,$6,$7::date,$8,$9,$10)"
                    else
                        "UPDATE claimcore.reminders SET revision=$3,snapshot=$4,state=$5,action=$6,due=$7::date,search_text=$8,changed_at=$9,recorded_by=$10 WHERE reminder_id=$1 AND case_id=$2"

                let! changed = DatabaseSession.execute connection sql parameters token

                if changed <> 1 then
                    raise (
                        RecordCorruptException
                            "Reminder projection is absent or has another parent."
                    )
            }

        task {
            match projection with
            | WorkChangeProjection.Work projection ->
                let value = projection.Snapshot
                let summary = value.Work.Summary |> Option.map HandlingSummary.format

                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.case_work SET revision=$2,snapshot=$3,responsible_actor=$4,handling_summary=$5,search_text=$6,changed_at=$7,recorded_by=$8 WHERE case_id=$1"
                        [|
                            box (CaseId.value value.CaseId)
                            box (Revision.value value.Revision)
                            box (WorkRecord.encodeSnapshot value)
                            DatabaseSession.parameter (
                                value.Work.Responsible |> Option.map ActorId.value
                            )
                            DatabaseSession.parameter summary
                            DatabaseSession.parameter (summary |> Option.map CaseSearch.normalize)
                            box projection.ChangedAt
                            box (ActorId.value projection.RecordedBy)
                        |]
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Work projection is absent.")

                let! _ =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.cases SET responsible_actor=$2,work_search_text=$3 WHERE case_id=$1"
                        [|
                            box (CaseId.value value.CaseId)
                            DatabaseSession.parameter (
                                value.Work.Responsible |> Option.map ActorId.value
                            )
                            DatabaseSession.parameter (summary |> Option.map CaseSearch.normalize)
                        |]
                        token

                return ()
            | WorkChangeProjection.Reminder projection ->
                let! parent =
                    task {
                        match projection.Effects with
                        | ReminderEffects.Created value ->
                            do! reminderValue projection.ChangedAt projection.RecordedBy true value
                            return Reminder.caseId value
                        | ReminderEffects.Changed value ->
                            do! reminderValue projection.ChangedAt projection.RecordedBy false value
                            return Reminder.caseId value
                        | ReminderEffects.CompletedAndPlanned(completed, next) ->
                            do!
                                reminderValue
                                    projection.ChangedAt
                                    projection.RecordedBy
                                    false
                                    completed

                            do! reminderValue projection.ChangedAt projection.RecordedBy true next
                            return Reminder.caseId completed
                    }

                let! _ =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.cases SET follow_up=(SELECT min(due) FROM claimcore.reminders WHERE case_id=$1 AND state='PENDING') WHERE case_id=$1"
                        [| box (CaseId.value parent) |]
                        token

                return ()
        }
