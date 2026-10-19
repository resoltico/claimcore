namespace ClaimCore.Application

open System
open ClaimCore.Application.VerificationChecks

/// Projections are comparison targets only after the ledger has been folded.
module internal ProjectionVerification =
    let private actors (rows: ActorSummary list) =
        rows
        |> List.fold
            (fun (actors: Map<Guid, ActorAuthority>) row ->
                let id = row.ActorId
                let principal = row.Principal

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
                        Enabled = row.Enabled
                        Grants = []
                    }

                Map.add id actor actors)
            Map.empty

    let private grants (actors: Map<Guid, ActorAuthority>) rows =
        rows
        |> List.fold
            (fun actors (id, role, caseId) ->

                let actor =
                    Map.tryFind id actors
                    |> Option.defaultWith (fun () ->
                        invalidOp "Grant target projection is absent.")

                let scope =
                    caseId
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

    let cases installationId histories projections =
        CaseFold.projection installationId histories projections

    let authority expected actorRows grantRows =
        capture None VerificationFinding.AuthorityProjection (fun () ->
            grants (actors actorRows) grantRows)
        |> Result.bind (fun actual ->
            require None VerificationFinding.AuthorityProjection (matching expected actual))
