module ClaimCore.IntegrationTests.DataAuditTests

open System
open System.IO
open System.Threading
open Npgsql
open NpgsqlTypes
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private audit app witness =
    use source = RuntimeDataSource.create app
    use connection = RuntimeDatabase.openConnection source
    DataAudit.run connection witness CancellationToken.None |> await

let private acceptedCaseAt (businessTime: IBusinessTime) owner app witness =
    let principal = human ("audit-owner-" + Guid.NewGuid().ToString("N"))
    provision owner witness principal |> applied
    use source = RuntimeDataSource.create app
    let grants = source
    let registry = new ActorGrantRegistry(source, witness)

    let editor =
        {
            Role = Role.CaseEditor
            Scope = GrantScope.Installation
        }

    registry.SetGrant(principal, actorId grants principal, editor, true)
    |> await
    |> applied

    let authority =
        load grants principal ResourceScope.Installation
        |> Option.defaultWith (fun () -> failtest "Synthetic audit owner is absent.")

    let input = newRequest ()

    let actorContext: ActorCallContext =
        {
            MayEditCommands = true
            AllowedRecoveryActions = []
            Binding =
                {
                    Principal = principal
                    ActorId = authority.ActorId
                    GrantRevision = authority.GrantRevision
                }
            CaseId = Some(Guid.NewGuid())
            Action = EndpointAction.ExecuteNewCase
            Suppression = FixturePrivateFiles.syntheticCommitments witness.Identity
        }

    FixtureCommandExecution.executeRequest source witness actorContext businessTime input
    |> await
    |> accepted
    |> ignore

    input

let private acceptedCase owner app witness = acceptedCaseAt clock owner app witness

let private snapshot owner operationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT snapshot FROM claimcore.case_changes WHERE operation_id=@operation",
            connection
        )

    command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId)
    |> ignore

    match command.ExecuteScalar() with
    | :? (byte array) as bytes -> bytes
    | _ -> failtest "Synthetic accepted snapshot is required."

let private replaceSnapshot owner operationId (bytes: byte array) =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET snapshot=@snapshot WHERE operation_id=@operation",
            connection
        )

    command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId)
    |> ignore

    command.Parameters.AddWithValue("snapshot", NpgsqlDbType.Bytea, bytes) |> ignore
    Expect.equal (command.ExecuteNonQuery()) 1 "Synthetic receipt changed once"

let private shiftDate owner operationId days =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET effective_business_date=effective_business_date+@days WHERE operation_id=@operation",
            connection
        )

    command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId)
    |> ignore

    command.Parameters.AddWithValue("days", NpgsqlDbType.Integer, days) |> ignore
    Expect.equal (command.ExecuteNonQuery()) 1 "Synthetic event date changed once"

let private setClaimant owner reference claimant =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.cases SET claimant_name=@claimant WHERE case_reference=@reference",
            connection
        )

    command.Parameters.AddWithValue("reference", NpgsqlDbType.Text, reference)
    |> ignore

    command.Parameters.AddWithValue("claimant", NpgsqlDbType.Text, claimant)
    |> ignore

    Expect.equal (command.ExecuteNonQuery()) 1 "Synthetic current row changed once"

let private expectsCorrupt app witness =
    Expect.throwsT<InvalidDataException>
        (fun () -> audit app witness |> ignore)
        "A divergent primary snapshot must fail full replay"

let private omittedCaseTest owner app witness =
    let input = acceptedCase owner app witness
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use omit =
        new NpgsqlCommand(
            "CREATE TEMP TABLE saved_audit_case AS SELECT * FROM claimcore.cases WHERE case_reference=@reference; "
            + "CREATE TEMP TABLE saved_audit_changes AS SELECT * FROM claimcore.case_changes WHERE case_reference=@reference; "
            + "DELETE FROM claimcore.case_changes WHERE case_reference=@reference; "
            + "DELETE FROM claimcore.cases WHERE case_reference=@reference",
            connection
        )

    omit.Parameters.AddWithValue("reference", NpgsqlDbType.Text, input.CaseReference)
    |> ignore

    omit.ExecuteNonQuery() |> ignore

    try
        expectsCorrupt app witness
    finally
        use restore =
            new NpgsqlCommand(
                "INSERT INTO claimcore.cases SELECT * FROM saved_audit_case; "
                + "INSERT INTO claimcore.case_changes SELECT * FROM saved_audit_changes",
                connection
            )

        restore.ExecuteNonQuery() |> ignore


let private changedCalendarRules owner app witness =
    // Independent rule datasets with the installation's same identifier put this instant
    // on different dates. The saved execution date belongs to acceptance, not today's rules.
    let instant = DateTimeOffset(2026, 9, 7, 23, 30, 0, TimeSpan.Zero)

    let zone offset =
        TimeZoneInfo.CreateCustomTimeZone("Etc/UTC", offset, "Synthetic", "Synthetic")

    let acceptedZone = zone (TimeSpan.FromHours 2.)
    let currentZone = zone TimeSpan.Zero

    let date zone =
        TimeZoneInfo.ConvertTime(instant, zone).DateTime |> DateOnly.FromDateTime

    Expect.equal (date acceptedZone) (DateOnly(2026, 9, 8)) "Accepted rules vector"
    Expect.equal (date currentZone) (DateOnly(2026, 9, 7)) "Current rules vector"

    let historicalTime =
        { new IBusinessTime with
            member _.Capture() =
                {
                    EffectiveBusinessDate = DateOnly(2026, 9, 8)
                    ObservedUtcInstant = instant
                    TimeZoneId = "Etc/UTC"
                }
        }

    acceptedCaseAt historicalTime owner app witness |> ignore
    let result = audit app witness
    Expect.isGreaterThan result.AcceptedOperations 0L "Authenticated historical context replays"

let private shiftInstant owner operationId seconds =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.case_changes SET observed_utc_instant=observed_utc_instant+make_interval(secs => @seconds) WHERE operation_id=@operation",
            connection
        )

    command.Parameters.AddWithValue("operation", NpgsqlDbType.Uuid, operationId)
    |> ignore

    command.Parameters.AddWithValue("seconds", NpgsqlDbType.Double, float seconds)
    |> ignore

    Expect.equal (command.ExecuteNonQuery()) 1 "Synthetic UTC observation changed once"

let tests =
    testList
        "full primary data audit"
        [
            testCase "[CC-AUDIT-001] audit replays every retained accepted case" (fun () ->
                withAuthorityDatabase (fun owner app witness ->
                    acceptedCase owner app witness |> ignore
                    let result = audit app witness
                    Expect.isGreaterThan result.Cases 0L "Audit saw cases"
                    Expect.isGreaterThan result.AcceptedOperations 0L "Audit saw accepted events"))
            testCase "[CC-AUDIT-001] audit rejects noncanonical historical snapshot" (fun () ->
                withAuthorityDatabase (fun owner app witness ->
                    let input = acceptedCase owner app witness
                    let original = snapshot owner input.OperationId
                    replaceSnapshot owner input.OperationId (Array.append original [| 32uy |])

                    try
                        expectsCorrupt app witness
                    finally
                        replaceSnapshot owner input.OperationId original))
            testCase "[CC-AUDIT-001] audit rejects a false accepted business date" (fun () ->
                withAuthorityDatabase (fun owner app witness ->
                    let input = acceptedCase owner app witness
                    shiftDate owner input.OperationId 1

                    try
                        expectsCorrupt app witness
                    finally
                        shiftDate owner input.OperationId -1))
            testCase
                "[CC-AUDIT-001] changed same-identity calendar rules preserve accepted history"
                (fun () -> withAuthorityDatabase changedCalendarRules)
            testCase "[CC-AUDIT-001] audit rejects a false accepted UTC observation" (fun () ->
                withAuthorityDatabase (fun owner app witness ->
                    let input = acceptedCase owner app witness
                    shiftInstant owner input.OperationId 1

                    try
                        expectsCorrupt app witness
                    finally
                        shiftInstant owner input.OperationId -1))
            testCase "[CC-AUDIT-001] audit rejects a stale current projection" (fun () ->
                withAuthorityDatabase (fun owner app witness ->
                    let input = acceptedCase owner app witness
                    setClaimant owner input.CaseReference "Synthetic altered claimant"

                    try
                        expectsCorrupt app witness
                    finally
                        setClaimant owner input.CaseReference registration.ClaimantName))
            testCase "[CC-AUDIT-001] independent witness detects a whole omitted case" (fun () ->
                withAuthorityDatabase omittedCaseTest)
        ]
    |> testSequenced
