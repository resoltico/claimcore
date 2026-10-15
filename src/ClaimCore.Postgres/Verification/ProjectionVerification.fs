namespace ClaimCore.Postgres

open System
open ClaimCore.Application
open ClaimCore.Postgres.VerificationChecks

/// Projections are comparison targets only after the ledger has been folded.
module internal ProjectionVerification =
    let private actors rows =
        rows
        |> List.fold
            (fun actors row ->
                let id = DatabaseSession.value<Guid> 0 row

                let principal =
                    PrincipalKey.fromStorage
                        (DatabaseSession.value 1 row)
                        (DatabaseSession.value 2 row)
                        (DatabaseSession.value 3 row)
                    |> Result.defaultWith (fun _ -> invalidOp "Invalid projected principal.")

                if
                    id = Guid.Empty
                    || Map.containsKey id actors
                    || (actors
                        |> Map.values
                        |> Seq.exists (fun actor -> actor.Principal = principal))
                then
                    invalidOp "Duplicate projected actor identity."

                let actor: ActorAuthority =
                    {
                        ActorId = id
                        Principal = principal
                        Enabled = DatabaseSession.value 4 row
                        Grants = []
                    }

                Map.add id actor actors)
            Map.empty

    let private grants (actors: Map<Guid, ActorAuthority>) rows =
        rows
        |> List.fold
            (fun actors row ->
                let id = DatabaseSession.value<Guid> 0 row

                let actor =
                    Map.tryFind id actors
                    |> Option.defaultWith (fun () ->
                        invalidOp "Grant target projection is absent.")

                let role =
                    Roles.tryParse (DatabaseSession.value<string> 1 row)
                    |> Option.defaultWith (fun () -> invalidOp "Invalid projected role.")

                let scope =
                    DatabaseSession.optional<Guid> 2 row
                    |> Option.map GrantScope.Case
                    |> Option.defaultValue GrantScope.Installation

                let grant: ActorGrant = { Role = role; Scope = scope }

                if List.contains grant actor.Grants || scope = GrantScope.Case Guid.Empty then
                    invalidOp "Invalid projected grant."

                Map.add
                    id
                    { actor with
                        Grants = grant :: actor.Grants
                    }
                    actors)
            actors

    let private matching expected actual =
        Map.count expected = Map.count actual
        && (expected
            |> Map.forall (fun id (actor: ActorAuthority) ->
                match Map.tryFind id actual with
                | None -> false
                | Some current ->
                    { actor with Grants = [] } = { current with Grants = [] }
                    && Set.ofList actor.Grants = Set.ofList current.Grants))

    let cases connection installationId histories token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    ($"SELECT {Projections.caseColumns} FROM claimcore.cases")
                    [||]
                    token

            return
                capture None VerificationFinding.CaseProjection (fun () ->
                    rows |> List.map Projections.caseRow)
                |> Result.bind (CaseFold.projection installationId histories)
        }

    let authority connection expected token =
        task {
            let! rows =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,principal_kind,issuer,subject,enabled FROM claimcore.actors ORDER BY actor_id"
                    [||]
                    token

            let! grantRows =
                DatabaseSession.rows
                    connection
                    "SELECT actor_id,role,case_id FROM claimcore.grants ORDER BY actor_id,role,case_id"
                    [||]
                    token

            return
                capture None VerificationFinding.AuthorityProjection (fun () ->
                    grants (actors rows) grantRows)
                |> Result.bind (fun actual ->
                    require None VerificationFinding.AuthorityProjection (matching expected actual))
        }
