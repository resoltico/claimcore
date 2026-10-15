namespace ClaimCore.Postgres

open ClaimCore.Application
open ClaimCore.RecordFormat

module internal WriteOperations =
    let case source settings installationId principal operationId reference decide token =
        Transactions.write source settings token (fun connection ->
            task {
                let! head, instant, nonce = Transactions.head connection token
                let! actor = Projections.actor connection principal token
                let! existing = StoredEntries.operation connection installationId operationId token

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

                match decide view with
                | LedgerDecision.Replay entry -> return LedgerWriteResult.Replayed entry, false
                | LedgerDecision.Refuse reason -> return LedgerWriteResult.Refused reason, false
                | LedgerDecision.Fail fault -> return LedgerWriteResult.Failed fault, false
                | LedgerDecision.Append(entry, projection, receipt) ->
                    let! _ = Persistence.entry connection entry token
                    do! Persistence.case connection projection token
                    return LedgerWriteResult.Appended receipt, true
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
                let! actor = Projections.actor connection principal token
                let! existing = StoredEntries.operation connection installationId operationId token
                let! target = Projections.actor connection target token

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

                match decide view with
                | LedgerDecision.Replay entry -> return LedgerWriteResult.Replayed entry, false
                | LedgerDecision.Refuse reason -> return LedgerWriteResult.Refused reason, false
                | LedgerDecision.Fail fault -> return LedgerWriteResult.Failed fault, false
                | LedgerDecision.Append(entry, projection, receipt) ->
                    let! _ = Persistence.entry connection entry token
                    do! Persistence.authority connection projection token
                    return LedgerWriteResult.Appended receipt, true
            })
