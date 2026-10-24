namespace ClaimCore.Postgres

open System.Threading.Tasks
open ClaimCore.Contract
open ClaimCore.Application
open ClaimCore.RecordFormat

/// Both snapshot reads and locked execution load the same authoritative decision facts.
module internal DecisionViews =
    let case connection installationId principal operationId reference instant token =
        task {
            let! existing =
                match operationId with
                | Some id -> StoredEntries.operation connection installationId id token
                | None -> Task.FromResult None

            let caseId = CaseReferenceId.encode installationId reference

            let! actor =
                Projections.actor
                    connection
                    principal
                    (caseId :: Projections.operationCases existing)
                    token

            let! record = Projections.caseById connection caseId token

            return
                ({
                    ObservedAt = instant
                    Actor = actor
                    ExistingOperation = existing
                    Case = record
                }
                : CaseReadView)
        }

    let private counts connection token =
        DatabaseSession.rows
            connection
            "SELECT (SELECT count(*)::int FROM claimcore.actors),count(*)::int FROM claimcore.actors a JOIN claimcore.grants g USING(actor_id) WHERE a.enabled AND a.principal_kind='HUMAN' AND g.role=$1 AND g.case_id IS NULL"
            [| box (RoleTokens.token Role.Owner) |]
            token

    let authority connection installationId principal operationId target reference instant token =
        task {
            let! existing = StoredEntries.operation connection installationId operationId token

            let ids =
                reference |> Option.map (CaseReferenceId.encode installationId) |> Option.toList

            let! actor =
                Projections.actor
                    connection
                    principal
                    (ids @ Projections.operationCases existing)
                    token

            let! target = Projections.actor connection target ids token

            let! record =
                match reference with
                | Some reference ->
                    Projections.caseById
                        connection
                        (CaseReferenceId.encode installationId reference)
                        token
                | None -> Task.FromResult None

            let! counts = counts connection token

            return
                ({
                    ObservedAt = instant
                    Actor = actor
                    ExistingOperation = existing
                    Target = target
                    TargetCase = record
                    ActorCount = DatabaseSession.value 0 counts.Head
                    EnabledHumanOwners = DatabaseSession.value 1 counts.Head
                }
                : AuthorityReadView)
        }
