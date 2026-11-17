module internal ClaimCore.ServerTests.OidcCoreFixture

open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Application.Storage
open ClaimCore.Application.Reads
open ClaimCore.Application.Execution
open ClaimCore.Application.Verification
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.TestSupport.StoreFixtures

open ClaimCore.TestSupport
open ClaimCore.Application

/// Real PostgreSQL preserves the same Application authorization exercised through OIDC.
type Fixture(database: PostgresFixture.Database) =
    member _.InstallationId = database.Fixture.InstallationId
    member _.Actor principal = core database.Fixture principal

    member _.Sessions =
        SessionStore(database.RuntimeSource, database.Installation) :> ISessionStore

    member _.SessionCount =
        use connection = database.OwnerSource.OpenConnection()

        use command =
            new Npgsql.NpgsqlCommand("SELECT count(*) FROM claimcore.sessions", connection)

        command.ExecuteScalar() :?> int64

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

            let registrationInput =
                Submissions.create
                    "people.register"
                    (id fixture 20)
                    (Binding.PeopleNew Revision.zero)
                    (Submissions.principal service
                     |> Map.fold
                         (fun values key value -> Map.add key value values)
                         (Submissions.profile DomainInputs.actorProfile))

            let! registered =
                Service.executeSubmission owner registrationInput CancellationToken.None

            acceptedPeople registered

            let! granted =
                grant
                    owner
                    (id fixture 21)
                    service
                    {
                        Role = Role.CaseReader
                        Scope = GrantTarget.Installation
                    }
                    true

            acceptedPeople granted

            let opening =
                request
                    fixture
                    10
                    "SYNTHETIC-001"
                    0L
                    "case.open"
                    (Submissions.fields (RegistrationInput.values registration)
                     |> Map.add
                         (Submissions.field BusinessField.CaseReference)
                         (ClaimCore.Application.Contracts.SubmissionValue.Text "SYNTHETIC-001"))

            let! opened = Service.executeSubmission owner opening CancellationToken.None

            accepted opened |> ignore
            return new Fixture(database)
        with error ->
            database.Fixture.Dispose()
            return raise error
    }

let create issuer = createFor issuer "synthetic-owner"
