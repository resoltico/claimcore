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
        task {
            let! usable = Credentials.admits tx principal
            let! facts = Projections.globalReadFacts tx.Connection principal tx.CancellationToken

            return
                if usable then
                    facts
                else
                    { facts with
                        People = AccessView.empty
                        AssignedRoles = []
                    }
        }

    let access (tx: ReadTx) principal caseId =
        task {
            match! Credentials.admits tx principal with
            | false -> return AccessView.empty
            | true ->
                match caseId with
                | Some id ->
                    return! Projections.actor tx.Connection principal [ id ] tx.CancellationToken
                | None ->
                    return!
                        Projections.actorWithVisibility tx.Connection principal tx.CancellationToken
        }

    let accessCases (tx: ReadTx) (principal, ids) =
        task {
            match! Credentials.admits tx principal with
            | false -> return AccessView.empty
            | true ->
                return!
                    Projections.actor
                        tx.Connection
                        principal
                        (List.distinct ids)
                        tx.CancellationToken
        }

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
