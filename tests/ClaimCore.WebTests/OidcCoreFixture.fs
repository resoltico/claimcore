module internal ClaimCore.WebTests.OidcCoreFixture

open ClaimCore.Protocol

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport
open ClaimCore.Postgres

/// Real PostgreSQL preserves the same Application authorization exercised through OIDC.
type Fixture(database: PostgresFixture.Database) =
    member _.Actor principal = core database.Fixture principal

    member _.EntryCount =
        use connection = database.OwnerSource.OpenConnection()

        use command =
            new Npgsql.NpgsqlCommand("SELECT sequence FROM claimcore.ledger_head", connection)

        command.ExecuteScalar() :?> int64 |> int

    interface IDisposable with
        member _.Dispose() = database.Fixture.Dispose()

let createFor issuer subject =
    task {
        let principal =
            PrincipalKey.human issuer subject |> Result.defaultWith (string >> invalidOp)

        let! database = PostgresFixture.createFor principal
        let fixture = database.Fixture

        try
            let! owner = caseWorker fixture

            let service =
                PrincipalKey.service issuer "service"
                |> Result.defaultWith (string >> invalidOp)

            let! registered =
                (Core.execute
                    owner
                    (OperationInput.Authority(
                        authorityRequest (id 20) service AuthorityCommandAct.Register
                    ))
                    (CancellationToken.None))

            acceptedAuthority registered

            let! granted =
                grant owner (id 21) service Role.CaseReader GrantTarget.Installation true

            acceptedAuthority granted

            let! opened =
                (Core.execute
                    owner
                    (OperationInput.Case(
                        request 10 "SYNTHETIC-001" 0L (UnvalidatedCaseAct.Open registration)
                    ))
                    (CancellationToken.None))

            accepted opened |> ignore
            return new Fixture(database)
        with error ->
            database.Fixture.Dispose()
            return raise error
    }

let create issuer = createFor issuer "synthetic-owner"
