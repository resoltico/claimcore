namespace ClaimCore.Application.Reads

open System
open System.Threading
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Application.Storage
open ClaimCore.RecordFormat
open Npgsql

module internal PeopleReads =
    type GrantQuery =
        {
            ActorId: ActorId
            AfterGrant: GrantPosition option
            Limit: int
        }

    type ActorQuery =
        {
            Role: Role option
            Text: DirectorySearchText option
            AfterActorId: ActorId option
            Limit: int
        }

    let peopleHistoryPage (tx: ReadTx) (query: TransactionPages.PeopleHistoryQuery) =
        let connection = tx.Connection
        let installation = tx.InstallationId
        let token = tx.CancellationToken
        TransactionPages.peopleHistory connection installation query token

    let accessRequestPage (tx: ReadTx) (query: AccessRequests.PageQuery) =
        let connection = tx.Connection
        let token = tx.CancellationToken
        AccessRequests.page connection query token

    let actorPage
        (tx: ReadTx)
        ({
             ActorQuery.AfterActorId = after
             Text = text
             Role = role
             Limit = limit
         })
        =
        let connection = tx.Connection
        let token = tx.CancellationToken
        TransactionPages.actors connection text role after limit token

    let grantPage
        (tx: ReadTx)
        ({
             GrantQuery.ActorId = id
             AfterGrant = after
             Limit = limit
         })
        =
        let connection = tx.Connection
        let token = tx.CancellationToken
        TransactionPages.grants connection id after limit token

    let actorRoleSummaries (tx: ReadTx) ids =
        let connection = tx.Connection
        let token = tx.CancellationToken
        Projections.actorRoleSummaries connection ids token

    let actorRefs (tx: ReadTx) (ids: ActorId list) =
        let connection = tx.Connection
        let token = tx.CancellationToken

        task {
            if ids.IsEmpty then
                return Map.empty
            else
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact FROM claimcore.actors WHERE actor_id=ANY($1::uuid[])"
                        [| box (ids |> List.distinct |> List.map ActorId.value |> List.toArray) |]
                        token

                return
                    rows
                    |> List.map (fun row ->
                        let actor = Projections.actorRow row in
                        actor.ActorId, Actor.currentReference actor)
                    |> Map.ofList
        }

    let actorPrincipal (tx: ReadTx) id =
        let connection = tx.Connection
        let token = tx.CancellationToken

        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT principal_kind,issuer,subject FROM claimcore.actors WHERE actor_id=$1"
                    [| box (ActorId.value id) |]
                    token

            match rows with
            | [] -> return None
            | [ row ] ->
                let kind =
                    DatabaseSession.value<string> 0 row
                    |> DatabaseSession.admitted (Vocabulary.tryParse PrincipalKind.vocabulary)

                return
                    PrincipalKey.ofKind
                        kind
                        (DatabaseSession.value<string> 1 row)
                        (DatabaseSession.value<string> 2 row)
                    |> Result.map Some
                    |> Result.defaultWith (fun _ ->
                        raise (RecordCorruptException "Invalid actor selection principal."))
            | _ -> return raise (RecordCorruptException "Duplicate actor selection rows.")
        }

    let peopleRevision (tx: ReadTx) principal =
        let connection = tx.Connection
        let token = tx.CancellationToken
        PeopleStateStorage.revision connection principal token

    let findActor (tx: ReadTx) id =
        let connection = tx.Connection
        let token = tx.CancellationToken

        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled,display_name,contact,digest_email FROM claimcore.actors WHERE actor_id=$1"
                    [| box (ActorId.value id) |]
                    token

            return rows |> List.tryExactlyOne |> Option.map (Projections.actorRowWithDigest 7)
        }

    let enabledHumanOwners (tx: ReadTx) () =
        let connection = tx.Connection
        let token = tx.CancellationToken

        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT count(*)::int FROM claimcore.actors a JOIN claimcore.grants g USING(actor_id) WHERE a.enabled AND a.principal_kind='HUMAN' AND g.role=$1 AND g.case_id IS NULL"
                    [| box (Role.token Role.Owner) |]
                    token

            return DatabaseSession.value<int> 0 rows.Head
        }

    let actorCount (tx: ReadTx) () =
        let connection = tx.Connection
        let token = tx.CancellationToken

        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT count(*)::int FROM claimcore.actors"
                    [||]
                    token

            return DatabaseSession.value<int> 0 rows.Head
        }
