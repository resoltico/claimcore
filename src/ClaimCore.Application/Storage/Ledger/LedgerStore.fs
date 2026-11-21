namespace ClaimCore.Application.Storage

open ClaimCore.Application.Verification

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application.Contracts

type internal LedgerStore
    (source: NpgsqlDataSource, installation: Installation, cipher: InstallationCipher, settings) =
    let installationId = installation.InstallationId

    static member InTransaction(connection, installationId, cipher, head, observedAt, token) =
        WriteTx(connection, installationId, cipher, head, observedAt, token)

    interface IStore with
        member _.Context query =
            Transactions.write source settings query.CancellationToken (fun connection ->
                task {
                    do! Baseline.requireExpected installation connection query.CancellationToken
                    // READ COMMITTED is essential: the advisory-lock statement may wait behind registration.
                    // The following single access query must acquire a fresh snapshot after that wait.
                    let! _ =
                        PrincipalLock.acquire connection query.Principal query.CancellationToken

                    let! observed =
                        DatabaseSession.rows
                            connection
                            "SELECT clock_timestamp()"
                            [||]
                            query.CancellationToken

                    let observedAt = DatabaseSession.instant 0 observed.Head

                    let! facts =
                        Projections.globalReadFacts
                            connection
                            query.Principal
                            query.CancellationToken

                    if facts.People.Actor.IsNone then
                        do!
                            AccessRequests.refresh
                                connection
                                query.Principal
                                query.Claims
                                observedAt
                                query.CancellationToken

                    return
                        {
                            ContextFacts.Facts = facts
                            ObservedAt = observedAt
                        },
                        true
                })

        member _.Read
            ({
                 ReadOperation.Work = work
                 CancellationToken = token
             })
            =
            Transactions.read source token (fun connection observedAt ->
                task {
                    do! Baseline.requireExpected installation connection token
                    return! work (ReadTx(connection, installationId, cipher, observedAt, token))
                })

        member _.RecordDisclosure(event, token) =
            Transactions.write source settings token (fun connection ->
                task {
                    do! Baseline.requireExpected installation connection token
                    let! _ = DisclosureStorage.append connection event token
                    return (), true
                })

        member _.Write
            ({
                 WriteOperation.Work = work
                 CancellationToken = token
             })
            =
            Transactions.write source settings token (fun connection ->
                task {
                    do! Baseline.requireExpected installation connection token
                    let! head, observedAt = Transactions.head connection token
                    FailureDiagnostics.phase FailurePhase.Read

                    let! verdict =
                        work (
                            WriteTx(connection, installationId, cipher, head, observedAt, token)

                        )

                    let commit =
                        match verdict with
                        | Verdict.Proceed _ -> true
                        | Verdict.Replay _
                        | Verdict.Refuse _
                        | Verdict.Fail _ -> false

                    return verdict, commit
                })
