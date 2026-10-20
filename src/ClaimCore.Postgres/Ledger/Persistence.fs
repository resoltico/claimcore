namespace ClaimCore.Postgres

open ClaimCore.Catalog

open System
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal Persistence =
    let entry connection (entry: EntryDraft) token =
        let scope, subject, revision, act, date =
            match entry.Scope with
            | StoredScope.Authority(target, kind) ->
                "AUTHORITY", target, None, RecordedAuthorityActs.token kind, None
            | StoredScope.Case(id, revision, kind, date) ->
                "CASE", id, Some revision, CaseActKinds.token kind, Some date

        DatabaseSession.execute
            connection
            "INSERT INTO claimcore.ledger(sequence,previous_hash,entry_hash,entry_format,scope,act,operation_id,actor_id,recorded_at,subject_id,case_revision,business_date,payload_commitment,payload,payload_nonce) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)"
            [|
                box entry.Sequence
                box entry.PreviousHash
                box entry.EntryHash
                box (int16 RecordVersions.LedgerEntry)
                box scope
                box act
                box entry.OperationId
                box entry.ActorId
                box entry.RecordedAt
                box subject
                DatabaseSession.parameter revision
                DatabaseSession.parameter date
                box entry.PayloadCommitment
                box entry.Payload
                box entry.PayloadNonce
            |]
            token

    let private snapshotParameters id (snapshot: CaseSnapshot) =
        let fields, disposition =
            match snapshot.View with
            | CaseView.Live(fields, Disposition.Active) -> fields, "ACTIVE"
            | CaseView.Live(fields, Disposition.Voided) -> fields, "VOIDED"
            | CaseView.Erased -> invalidArg (nameof snapshot) "Use the erasure projection."

        let date text =
            DateOnly.ParseExact(text, "yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture)

        let amount (text: string) =
            Decimal.Parse(text, Globalization.CultureInfo.InvariantCulture)

        [|
            box id
            box fields.CaseReference
            box snapshot.Revision
            box disposition
            box (date fields.IncidentDate)
            box (date fields.IncidentNotificationDate)
            box fields.IncidentCountry
            box fields.ClaimantName
            box fields.InsurerName
            box (amount fields.ClaimedAmount)
            box fields.ClaimedCurrency
            DatabaseSession.parameter (fields.PaymentDecisionDate |> Option.map date)
            DatabaseSession.parameter (fields.PayableAmount |> Option.map amount)
            DatabaseSession.parameter fields.PayableCurrency
            DatabaseSession.parameter (fields.PaymentDate |> Option.map date)
            box (CaseStatuses.token fields.Status)
        |]

    let private updateCase =
        "UPDATE claimcore.cases SET case_reference=$2,revision=$3,disposition=$4,incident_date=$5,incident_notification_date=$6,incident_country=$7,claimant_name=$8,insurer_name=$9,claimed_amount=$10,claimed_currency=$11,payment_decision_date=$12,payable_amount=$13,payable_currency=$14,payment_date=$15,status=$16 WHERE case_id=$1"

    let case connection projection token =
        task {
            match projection with
            | CaseProjection.Insert(id, snapshot) ->
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.cases VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16)"
                        (snapshotParameters id snapshot)
                        token

                return ()
            | CaseProjection.Update(id, snapshot) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        updateCase
                        (snapshotParameters id snapshot)
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Case projection is absent.")
            | CaseProjection.Erase(id, revision) ->
                let! _ =
                    DatabaseSession.rows
                        connection
                        "SELECT claimcore.erase_case_payload($1)"
                        [| box id |]
                        token

                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.cases SET revision=$2,disposition='ERASED',incident_date=NULL,incident_notification_date=NULL,incident_country=NULL,claimant_name=NULL,insurer_name=NULL,claimed_amount=NULL,claimed_currency=NULL,payment_decision_date=NULL,payable_amount=NULL,payable_currency=NULL,payment_date=NULL,status=NULL WHERE case_id=$1"
                        [| box id; box revision |]
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Case projection is absent.")
        }

    let private register connection id principal token =
        let kind, issuer, subject = PrincipalKey.storageParts principal

        DatabaseSession.execute
            connection
            "INSERT INTO claimcore.actors VALUES ($1,$2,$3,$4,true)"
            [| box id; box kind; box issuer; box subject |]
            token

    let authority connection projection token =
        task {
            match projection with
            | AuthorityProjection.RegisterActor(id, principal) ->
                let! _ = register connection id principal token
                return ()
            | AuthorityProjection.ProvisionOwner(id, principal) ->
                let! _ = register connection id principal token

                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.grants VALUES ($1,$2,NULL)"
                        [| box id; box (RoleTokens.token Role.Owner) |]
                        token

                return ()
            | AuthorityProjection.SetEnabled(id, enabled) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        "UPDATE claimcore.actors SET enabled=$2 WHERE actor_id=$1"
                        [| box id; box enabled |]
                        token

                if changed <> 1 then
                    raise (RecordCorruptException "Authority projection is absent.")
            | AuthorityProjection.AddGrant(id, role, caseId) ->
                let! _ =
                    DatabaseSession.execute
                        connection
                        "INSERT INTO claimcore.grants VALUES ($1,$2,$3) ON CONFLICT DO NOTHING"
                        [| box id; box (RoleTokens.token role); DatabaseSession.parameter caseId |]
                        token

                return ()
            | AuthorityProjection.RemoveGrant(id, role, caseId) ->
                let! _ =
                    DatabaseSession.execute
                        connection
                        "DELETE FROM claimcore.grants WHERE actor_id=$1 AND role=$2 AND case_id IS NOT DISTINCT FROM $3"
                        [| box id; box (RoleTokens.token role); DatabaseSession.parameter caseId |]
                        token

                return ()
        }
