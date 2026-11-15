namespace ClaimCore.Application.Reads

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open Npgsql

module internal AccessReads =
    type AssigneeQuery =
        {
            CaseId: CaseId
            ReadRoles: Role list
            WorkRoles: Role list
            AfterActorId: ActorId option
            Limit: int
        }

    let globalReadFacts (tx: ReadTx) principal =
        let connection = tx.Connection
        let token = tx.CancellationToken
        Projections.globalReadFacts connection principal token

    let access (tx: ReadTx) principal caseId =
        let connection = tx.Connection
        let token = tx.CancellationToken

        match caseId with
        | Some id -> Projections.actor connection principal [ id ] token
        | None -> Projections.actorWithVisibility connection principal token

    let accessCases (tx: ReadTx) (principal, ids) =
        let connection = tx.Connection
        let token = tx.CancellationToken
        Projections.actor connection principal (List.distinct ids) token

    let assigneePage (tx: ReadTx) (query: AssigneeQuery) =
        let connection = tx.Connection
        let token = tx.CancellationToken

        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT a.actor_id,a.principal_kind,a.issuer,a.subject,a.enabled,a.display_name,a.contact FROM claimcore.actors a WHERE a.enabled AND a.principal_kind='HUMAN' AND ($4::uuid IS NULL OR a.actor_id>$4) AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=$1) AND g.role=ANY($2::text[])) AND EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id=a.actor_id AND (g.case_id IS NULL OR g.case_id=$1) AND g.role=ANY($3::text[])) ORDER BY a.actor_id LIMIT $5"
                    [|
                        box (CaseId.value query.CaseId)
                        box (query.ReadRoles |> List.map Role.token |> List.toArray)
                        box (query.WorkRoles |> List.map Role.token |> List.toArray)
                        DatabaseSession.parameter (query.AfterActorId |> Option.map ActorId.value)
                        box (query.Limit + 1)
                    |]
                    token

            return
                rows
                |> List.map (Projections.actorRow >> Actor.currentReference)
                |> TransactionPages.page query.Limit _.ActorId
        }
