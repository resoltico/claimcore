namespace ClaimCore.Postgres

open ClaimCore.Contract

open ClaimCore.Application

module internal WriteOperations =
    let private isFenced connection (actor: AuthorityView) operationId token =
        task {
            match actor.Actor with
            | None -> return false
            | Some actor ->
                let! rows =
                    DatabaseSession.rows
                        connection
                        "SELECT EXISTS (SELECT 1 FROM claimcore.operation_fences WHERE actor_id=$1 AND operation_id=$2)"
                        [| box actor.ActorId; box operationId |]
                        token

                return DatabaseSession.value 0 rows.Head
        }

    let resolve source settings installationId principal operationId decide token =
        Transactions.operational source settings token (fun connection ->
            task {
                let! _, instant, _ = Transactions.head connection token

                let! view =
                    DecisionViews.operation
                        connection
                        installationId
                        principal
                        operationId
                        instant
                        token

                let result = decide view

                match result with
                | ResolveReply.NotAccepted ->
                    let! _ =
                        DatabaseSession.execute
                            connection
                            "INSERT INTO claimcore.operation_fences(actor_id,operation_id,fenced_at) VALUES ($1,$2,$3) ON CONFLICT (actor_id,operation_id) DO NOTHING"
                            [| box view.Actor.Actor.Value.ActorId; box operationId; box instant |]
                            token

                    return Transactions.OperationalDecision.Persist result
                | _ -> return Transactions.OperationalDecision.ReadOnly result
            })

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

                let! read =
                    DecisionViews.case
                        connection
                        installationId
                        principal
                        (Some operationId)
                        reference
                        instant
                        token

                let! fenced = isFenced connection read.Actor operationId token

                let view: CaseLockedView =
                    {
                        IsFenced = fenced
                        Head = head
                        PayloadNonce = nonce
                        Read = read
                    }

                return! persist connection token Persistence.case (decide view)
            })

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

                let! read =
                    DecisionViews.authority
                        connection
                        installationId
                        principal
                        operationId
                        target
                        reference
                        instant
                        token

                let! fenced = isFenced connection read.Actor operationId token

                let view: AuthorityLockedView =
                    {
                        IsFenced = fenced
                        Head = head
                        PayloadNonce = nonce
                        Read = read
                    }

                return! persist connection token Persistence.authority (decide view)
            })
