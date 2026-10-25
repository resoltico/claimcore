namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Application.VerificationChecks

/// Projections are comparison targets only after the ledger has been folded.
module internal ProjectionVerification =
    let actor (actors: Map<Guid, ActorAuthority>) (row: Actor) =
        result {
            let id = row.ActorId
            let principal = row.Principal

            do!
                require
                    None
                    VerificationFinding.AuthorityProjection
                    (id <> Guid.Empty
                     && not (Map.containsKey id actors)
                     && not (
                         actors
                         |> Map.values
                         |> Seq.exists (fun actor -> actor.Principal = principal)
                     ))

            let actor: ActorAuthority =
                {
                    ActorId = id
                    Principal = principal
                    Enabled = row.Enabled
                    Grants = []
                }

            return Map.add id actor actors
        }

    let grant (actors: Map<Guid, ActorAuthority>) (id, role, caseId) =
        result {
            let! actor =
                Map.tryFind id actors |> known None VerificationFinding.AuthorityProjection

            let scope =
                caseId
                |> Option.map GrantScope.Case
                |> Option.defaultValue GrantScope.Installation

            let grant: ActorGrant = { Role = role; Scope = scope }

            do!
                require
                    None
                    VerificationFinding.AuthorityProjection
                    (not (List.contains grant actor.Grants) && scope <> GrantScope.Case Guid.Empty)

            return
                Map.add
                    id
                    { actor with
                        Grants = grant :: actor.Grants
                    }
                    actors
        }

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

    let caseProjection installationId histories seen (record: StoredCase) =
        require
            None
            VerificationFinding.CaseProjection
            (not (Set.contains record.CaseId seen)
             && CaseFold.matchesProjection installationId histories record)
        |> Result.map (fun () -> Set.add record.CaseId seen)

    let finishCases histories seen =
        require None VerificationFinding.CaseProjection (Map.count histories = Set.count seen)

    let finishAuthority expected actual =
        require None VerificationFinding.AuthorityProjection (matching expected actual)

    let authority expected actorRows grantRows =
        actorRows
        |> List.fold
            (fun state row -> Result.bind (fun state -> actor state row) state)
            (Ok Map.empty)
        |> Result.bind (fun actors ->
            grantRows
            |> List.fold
                (fun state row -> Result.bind (fun state -> grant state row) state)
                (Ok actors))
        |> Result.bind (finishAuthority expected)
