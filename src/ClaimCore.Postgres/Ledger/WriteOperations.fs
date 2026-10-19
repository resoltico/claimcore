namespace ClaimCore.Postgres

open ClaimCore.Application
open ClaimCore.RecordFormat

module internal WriteOperations =
    let private persist connection token project decision =
        task {
            match decision with
            | LedgerDecision.Replay entry -> return LedgerWriteResult.Replayed entry, false
            | LedgerDecision.Refuse reason -> return LedgerWriteResult.Refused reason, false
            | LedgerDecision.Fail fault -> return LedgerWriteResult.Failed fault, false
            | LedgerDecision.Append(entry, projection, receipt) ->
                let! _ = Persistence.entry connection entry token
                do! project connection projection token
                return LedgerWriteResult.Appended receipt, true
        }

    let case source settings installationId principal operationId reference decide token =
        Transactions.write source settings token (fun connection ->
            task {
                let! head, instant, nonce = Transactions.head connection token
                let! existing = StoredEntries.operation connection installationId operationId token

                let ids =
                    CaseReferenceId.encode installationId reference
                    :: Projections.operationCases existing

                let! actor = Projections.actor connection principal ids token

                let! case =
                    Projections.caseById
                        connection
                        (CaseReferenceId.encode installationId reference)
                        token

                let view: CaseLockedView =
                    {
                        Head = head
                        ObservedAt = instant
                        PayloadNonce = nonce
                        Actor = actor
                        ExistingOperation = existing
                        Case = case
                    }

                return! persist connection token Persistence.case (decide view)
            })

    let private counts connection token =
        DatabaseSession.rows
            connection
            "SELECT (SELECT count(*)::int FROM claimcore.actors),count(*)::int FROM claimcore.actors a JOIN claimcore.grants g USING(actor_id) WHERE a.enabled AND a.principal_kind='HUMAN' AND g.role=$1 AND g.case_id IS NULL"
            [| box (Roles.token Role.Owner) |]
            token

    let authority
        source
        settings
        installationId
        principal
        operationId
        target
        reference
        decide
        token
        =
        Transactions.write source settings token (fun connection ->
            task {
                let! head, instant, nonce = Transactions.head connection token
                let! existing = StoredEntries.operation connection installationId operationId token

                let ids =
                    reference
                    |> Option.map (CaseReferenceId.encode installationId)
                    |> Option.toList

                let! actor =
                    Projections.actor
                        connection
                        principal
                        (ids @ Projections.operationCases existing)
                        token

                let! target = Projections.actor connection target ids token

                let! case =
                    match reference with
                    | Some reference ->
                        Projections.caseById
                            connection
                            (CaseReferenceId.encode installationId reference)
                            token
                    | None -> System.Threading.Tasks.Task.FromResult None

                let! counts = counts connection token

                let view: AuthorityLockedView =
                    {
                        Head = head
                        ObservedAt = instant
                        PayloadNonce = nonce
                        Actor = actor
                        ExistingOperation = existing
                        Target = target
                        TargetCase = case
                        ActorCount = DatabaseSession.value 0 counts.Head
                        EnabledHumanOwners = DatabaseSession.value 1 counts.Head
                    }

                return! persist connection token Persistence.authority (decide view)
            })
