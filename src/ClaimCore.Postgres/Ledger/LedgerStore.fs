namespace ClaimCore.Postgres

open ClaimCore.Application
open Npgsql

type internal LedgerStore(source: NpgsqlDataSource, installationId, settings) =
    interface ILedgerStore with
        member _.WriteCase(principal, id, reference, decide, token) =
            WriteOperations.case source settings installationId principal id reference decide token

        member _.WriteAuthority(principal, id, target, reference, decide, token) =
            WriteOperations.authority
                source
                settings
                installationId
                principal
                id
                target
                reference
                decide
                token

        member _.ReadCase(principal, id, reference, token) =
            ReadOperations.case source installationId principal id reference token

        member _.ReadActor(principal, token) =
            ReadOperations.actor source principal token

        member _.ReadOperation(principal, id, token) =
            ReadOperations.operation source installationId principal id token

        member _.ListCases(principal, visibility, after, limit, token) =
            ReadOperations.list source principal visibility after limit token

        member _.ReadHistory(principal, reference, after, limit, token) =
            ReadOperations.history source installationId principal reference after limit token
