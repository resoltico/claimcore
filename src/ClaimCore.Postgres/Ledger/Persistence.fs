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
                }
            | EntryTarget.Authority(target, kind) ->
                {
                    Kind = "AUTHORITY"
                    Id = ActorId.value target
                    Revision = None
                    Act = DurableNames.authorityAct kind
                    Date = None
                }
            | EntryTarget.Case(id, revision, kind, date) ->
                {
                    Kind = "CASE"
                    Id = CaseId.value id
                    Revision = Some revision
                    Act = DurableNames.caseAct kind
                    Date = Some date
                }

        DatabaseSession.execute
            connection
            "INSERT INTO claimcore.ledger(sequence,previous_hash,entry_hash,entry_format,subject_kind,act,operation_id,actor_id,recorded_at,subject_id,case_revision,business_date,payload_commitment,payload,payload_nonce,creation_reference) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)"
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

                return ()
            | CaseProjection.Update(id, snapshot, index) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.cases SET case_reference=$2,revision=$3,state=$4,snapshot=$5,status=$6,progress=$7,changed_at=$8,search_text=$9,notified_on=$10,claimed_amount=$11,claimed_currency=$12,payable_amount=$13,payable_currency=$14,responsible_actor=$15,follow_up=$16 WHERE case_id=$1"
                        (caseParameters id snapshot index)
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
                        "UPDATE claimcore.cases SET revision=$2,state='ERASED',snapshot=NULL,status=NULL,progress=NULL,search_text=NULL,notified_on=NULL,claimed_amount=NULL,claimed_currency=NULL,payable_amount=NULL,payable_currency=NULL,responsible_actor=NULL,follow_up=NULL,changed_at=$3 WHERE case_id=$1"
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
