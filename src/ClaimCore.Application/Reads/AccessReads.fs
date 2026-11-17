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
            let eligibility =
                AssigneeSelectionSql.predicate
                    Access.assigneeSelection
                    "$2"
                    AssigneeSelectionSql.Source.SelectedCase

            let! rows =
                DatabaseSession.rows
                    connection
                    $"SELECT a.actor_id,a.principal_kind,a.issuer,a.subject,a.enabled,a.display_name,a.contact FROM claimcore.actors a WHERE {eligibility} AND ($3::uuid IS NULL OR a.actor_id>$3) ORDER BY a.actor_id LIMIT $4"
                    [|
                        box (CaseId.value query.CaseId)
                        AssigneeSelectionSql.parameters Access.assigneeSelection
                        DatabaseSession.parameter (query.AfterActorId |> Option.map ActorId.value)
                        box (query.Limit + 1)
                    |]
                    token

            return
                rows
                |> List.map (Projections.actorRow >> Actor.currentReference)
                |> TransactionPages.page query.Limit _.ActorId
        }
