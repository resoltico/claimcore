module internal ClaimCore.IntegrationTests.FixtureActorAuthority

open System.Data
open System.Threading
open Npgsql
open ClaimCore.Postgres

/// Fixture identity setup uses production read primitives, not a second authorization gate.
let load (source: NpgsqlDataSource) principal scope =
    task {
        use! connection = RuntimeDatabase.openConnectionAsync source
        use! transaction = connection.BeginTransactionAsync(IsolationLevel.ReadCommitted)

        let! revision =
            ActorGrantRead.lockRevision connection transaction false CancellationToken.None

        return!
            ActorGrantRead.loadUnderLock
                connection
                transaction
                principal
                scope
                revision
                CancellationToken.None
    }
    |> fun pending -> pending.GetAwaiter().GetResult()
