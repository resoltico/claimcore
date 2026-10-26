namespace ClaimCore.Postgres

open ClaimCore.Contract

open System
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal Persistence =
    let entry connection (entry: EntryDraft) token =
        let subjectKind, subjectId, revision, act, date =
            match entry.Subject with
            | StoredSubject.Authority(target, kind) ->
                "AUTHORITY", target, None, RecordedAuthorityActs.token kind, None
            | StoredSubject.Case(id, revision, kind, date) ->
                "CASE", id, Some revision, CaseActKinds.token kind, Some date

        DatabaseSession.execute
            connection
            "INSERT INTO claimcore.ledger(sequence,previous_hash,entry_hash,entry_format,subject_kind,act,operation_id,actor_id,recorded_at,subject_id,case_revision,business_date,payload_commitment,payload,payload_nonce) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)"
            [|
                box entry.Sequence
                box entry.PreviousHash
                box entry.EntryHash
                box (int16 RecordVersions.LedgerEntry)
                box subjectKind
                box act
                box entry.OperationId
                box entry.ActorId
                box entry.RecordedAt
                box subjectId
                DatabaseSession.parameter revision
                DatabaseSession.parameter date
                box entry.PayloadCommitment
                box entry.Payload
                box entry.PayloadNonce
            |]
            token

    let case connection projection token =
        task {
            match projection with
            | CaseProjection.Insert(id, snapshot) ->
                let! _ =
                    DatabaseSession.execute
                        connection
                        CaseProjectionLayout.insert
                        (CaseProjectionLayout.parameters id snapshot)
                        token

                return ()
            | CaseProjection.Update(id, snapshot) ->
                let! changed =
                    DatabaseSession.execute
                        connection
                        CaseProjectionLayout.update
                        (CaseProjectionLayout.parameters id snapshot)
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
                        CaseProjectionLayout.erase
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
            | AuthorityProjection.Register(id, principal) ->
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
