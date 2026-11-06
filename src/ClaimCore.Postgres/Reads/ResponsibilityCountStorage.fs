namespace ClaimCore.Postgres

open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts

/// Count within the viewer's existing selection and return an explicit known zero for each requested actor.
module internal ResponsibilityCountStorage =
    let counts connection (selection: CaseSelection) (actors: ActorId list) token =
        task {
            if actors.Length > Paging.MaximumPageSize then
                invalidArg (nameof actors) "Use at most one admitted actor page."

            if actors.IsEmpty then
                return Map.empty
            else
                let! rows =
                    DatabaseSession.namedRows
                        connection
                        ($"SELECT requested.actor_id,count(c.case_id) AS cases FROM unnest($13::uuid[]) AS requested(actor_id) LEFT JOIN (SELECT c.case_id,c.responsible_actor FROM claimcore.cases c WHERE {CaseSelectionSql.predicate selection}) c ON c.responsible_actor=requested.actor_id GROUP BY requested.actor_id")
                        (Array.append
                            (CaseSelectionSql.parameters selection)
                            [|
                                box (
                                    actors
                                    |> List.distinct
                                    |> List.map ActorId.value
                                    |> List.toArray
                                )
                            |])
                        token

                return
                    rows
                    |> List.map (fun row ->
                        DatabaseSession.namedValue<System.Guid> "actor_id" row
                        |> DatabaseSession.admitted ActorId.create,
                        DatabaseSession.namedValue<int64> "cases" row)
                    |> Map.ofList
        }
