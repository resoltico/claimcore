namespace ClaimCore.Postgres

open ClaimCore.Application

module internal WriteOperations =
    let case source settings installationId principal operationId reference decide token =
        Transactions.write source settings token (fun connection ->
            task {
                let! head, instant, nonce = Transactions.head connection token
                let! actor = Projections.actor connection principal token
                let! existing = StoredEntries.operation connection installationId operationId token
                let! case = Projections.caseBy connection "case_reference" (box reference) token

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
                | CaseDecision.Replay entry -> return CaseWriteResult.Replayed entry, false
                | CaseDecision.Refuse reason -> return CaseWriteResult.Refused reason, false
                | CaseDecision.Fail fault -> return CaseWriteResult.Failed fault, false
                | CaseDecision.Append(entry, projection) ->
                    let! _ = Persistence.entry connection entry token
                    do! Persistence.case connection projection token
                    return CaseWriteResult.Appended entry, true
            })

    let authority source settings installationId principal eventId target reference decide token =
        Transactions.write source settings token (fun connection ->
            task {
                let! head, instant, nonce = Transactions.head connection token
                let! actor = Projections.actor connection principal token
                let! existing = StoredEntries.operation connection installationId eventId token
                let! target = Projections.actor connection target token

                let! case =
                    match reference with
                    | Some reference ->
                        Projections.caseBy connection "case_reference" (box reference) token
                    | None -> System.Threading.Tasks.Task.FromResult None

                let! counts =
                    DatabaseSession.rows
                        connection
                        "SELECT (SELECT count(*)::int FROM claimcore.actors),count(*)::int FROM claimcore.actors a JOIN claimcore.grants g USING(actor_id) WHERE a.enabled AND a.principal_kind='HUMAN' AND g.role='OWNER' AND g.case_id IS NULL"
                        [||]
                        token

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
                | AuthorityDecision.Replay entry ->
                    return AuthorityWriteResult.Replayed entry, false
                | AuthorityDecision.Refuse reason ->
                    return AuthorityWriteResult.Refused reason, false
                | AuthorityDecision.Fail fault -> return AuthorityWriteResult.Failed fault, false
                | AuthorityDecision.Append(entry, projection) ->
                    let! _ = Persistence.entry connection entry token
                    do! Persistence.authority connection projection token
                    return AuthorityWriteResult.Appended entry, true
            })
