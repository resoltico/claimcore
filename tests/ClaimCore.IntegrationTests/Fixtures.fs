module ClaimCore.IntegrationTests.Fixtures

open System
open Npgsql
open NpgsqlTypes
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Hosting

let completedAdministration outcome =
    FixtureDatabase.completedAdministration outcome

let refusedAdministration expected outcome =
    FixtureDatabase.refusedAdministration expected outcome

let accepted result = FixtureDatabase.accepted result
let await operation = FixtureDatabase.await operation
let syntheticSuppressionCheck = FixtureDatabase.syntheticSuppressionCheck
let appConnection () = FixtureDatabase.appConnection ()
let adminConnection () = FixtureDatabase.adminConnection ()
let witnessConnection () = FixtureDatabase.witnessConnection ()

let witnessAuditConnection () =
    FixtureDatabase.witnessAuditConnection ()

let internal containerIds () = FixtureDatabase.containerIds ()

let witnessOwnerConnection () =
    FixtureDatabase.witnessOwnerConnection ()

let witnessKey () = FixtureDatabase.witnessKey ()
let witnessKeyId () = FixtureDatabase.witnessKeyId ()
let internal witnessProtocol () = FixtureDatabase.witnessProtocol ()

let witnessStore writer identity =
    FixtureDatabase.witnessStore writer identity

let writerCapabilityFile () = FixtureDatabase.writerCapabilityFile ()

let witnessedOpen primary cancellation =
    FixtureDatabase.witnessedOpen primary cancellation

let appConnectionFile () = FixtureDatabase.appConnectionFile ()
let suppressionKeyFile () = FixtureDatabase.suppressionKeyFile ()
let artifactKeyRingFile () = FixtureDatabase.artifactKeyRingFile ()
let shutdown () = FixtureDatabase.shutdown ()
let internal store () = ActorBoundStoreFixture.create ()

let actorCore (runtime: Runtime) =
    runtime.ForActor(ActorBoundStoreFixture.actorPrincipal ())

let internal clock =
    { new IBusinessTime with
        member _.Capture() =
            {
                ObservedUtcInstant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
                EffectiveBusinessDate = DateOnly(2026, 9, 7)
                TimeZoneId = "Etc/UTC"
            }
    }

let internal recoveryPageLimit = SemanticContract.current.MaximumPageSize

let utcMicrosecond (value: DateTimeOffset) =
    DateTimeOffset(value.Ticks - value.Ticks % 10L, TimeSpan.Zero)

let registration =
    {
        IncidentDate = "2026-08-01"
        IncidentNotificationDate = "2026-08-03"
        IncidentCountry = "Lithuania"
        ClaimantName = "Integration Test Company"
        InsurerName = "Alleged Test Insurer"
        ClaimedAmount = "1000"
        ClaimedCurrency = "EUR"
    }

let newRequest () : CommandRequest =
    {
        OperationId = Guid.NewGuid()
        CaseReference = "TEST-" + Guid.NewGuid().ToString("N")
        ExpectedVersion = 0L
        Command = Command.Open registration
    }

/// Native tests cross the core boundary with a closed domain request, never an adapter draft.
let openRequest operationId caseReference : CommandRequest =
    {
        OperationId = operationId
        CaseReference = caseReference
        ExpectedVersion = 0L
        Command = Command.Open registration
    }

let next (previous: CommandRequest) version command =
    { previous with
        OperationId = Guid.NewGuid()
        ExpectedVersion = version
        Command = command
    }

let runSql connectionString sql reference =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    let parameter = command.Parameters.Add("reference", NpgsqlDbType.Text)
    parameter.Value <- reference
    command.ExecuteNonQuery() |> ignore

/// Both independently supplied connections must name the same explicit test endpoint.
/// This catches setup mistakes, not malicious routing or proof that all existing data is synthetic.
let requireSameTarget (application: string) (owner: string) =
    let app = NpgsqlConnectionStringBuilder(application)
    let admin = NpgsqlConnectionStringBuilder(owner)
    let host = app.Host |> Option.ofObj |> Option.defaultValue ""
    let database = app.Database |> Option.ofObj |> Option.defaultValue ""
    let appUsername = app.Username |> Option.ofObj |> Option.defaultValue ""
    let adminHost = admin.Host |> Option.ofObj |> Option.defaultValue ""
    let adminDatabase = admin.Database |> Option.ofObj |> Option.defaultValue ""
    let adminUsername = admin.Username |> Option.ofObj |> Option.defaultValue ""

    if
        String.IsNullOrWhiteSpace(host)
        || host.Contains(',')
        || not (String.Equals(host, adminHost, StringComparison.OrdinalIgnoreCase))
        || app.Port <> admin.Port
        || database <> adminDatabase
        || String.IsNullOrWhiteSpace(database)
        || not (database.EndsWith("_test", StringComparison.Ordinal))
        || appUsername <> "claimcore_app"
        || String.IsNullOrWhiteSpace(adminUsername)
        || adminUsername = appUsername
    then
        invalidOp
            "Integration connections must name one explicit identical test host/port/database with distinct runtime and owner roles."

let validateTargets () =
    requireSameTarget (appConnection ()) (adminConnection ())
