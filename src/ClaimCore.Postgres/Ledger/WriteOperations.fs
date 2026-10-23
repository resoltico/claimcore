namespace ClaimCore.Postgres

open ClaimCore.Contract

open ClaimCore.Application

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

                let! read =
                    DecisionViews.case
                        connection
                        installationId
                        principal
                        (Some operationId)
                        reference
                        instant
                        token

                let view: CaseLockedView =
                    {
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

                let view: AuthorityLockedView =
                    {
                        Head = head
                        PayloadNonce = nonce
                        Read = read
                    }

                return! persist connection token Persistence.authority (decide view)
            })
