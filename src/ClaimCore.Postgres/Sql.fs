namespace ClaimCore.Postgres

open System
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Application
open ClaimCore.RecordFormat

module internal Sql =
    let private columns =
        [
            ScalarEncoding.dateColumn "incident_date"
            ScalarEncoding.dateColumn "incident_notification_date"
            "c.incident_country"
            "c.claimant_name"
            "c.insurer_name"
            "c.claimed_amount::text AS claimed_amount"
            "c.claimed_currency"
            "c.case_reference"
            ScalarEncoding.dateColumn "payment_decision_date"
            "c.payable_amount::text AS payable_amount"
            "c.payable_currency"
            ScalarEncoding.dateColumn "payment_date"
            "c.status"
            "c.revision"
        ]
        |> String.concat ", "

    let private source = " FROM claimcore.cases c"

    let selectCase =
        "SELECT " + columns + source + " WHERE c.case_reference = @reference"

    let private pageWindow = string (SemanticContract.current.MaximumPageSize + 1)

    let listCases =
        "SELECT "
        + columns
        + source
        + " WHERE (@after IS NULL OR c.case_reference > @after COLLATE \"C\") ORDER BY c.case_reference COLLATE \"C\" LIMIT "
        + pageWindow

    let private receiptColumns =
        "operation_id, case_reference, revision, command_name, request_format_version, request_sha256, "
        + "snapshot_version, snapshot, recorded_at, recorded_by"

    let history =
        "SELECT "
        + receiptColumns
        + " FROM claimcore.case_changes WHERE case_reference = @reference AND revision > @after ORDER BY revision LIMIT "
        + pageWindow

    let operation =
        "SELECT "
        + receiptColumns
        + " FROM claimcore.case_changes WHERE operation_id = @operation"

    let private withDateOperands (sql: string) =
        [ "incident"; "notification"; "decision"; "paid" ]
        |> List.fold
            (fun (text: string) name ->
                text.Replace(
                    "@" + name,
                    ScalarEncoding.dateParameter name,
                    StringComparison.Ordinal
                ))
            sql

    let insertCase =
        """
        INSERT INTO claimcore.cases (
            incident_date, incident_notification_date, incident_country, claimant_name, insurer_name,
            claimed_amount, claimed_currency, case_reference, payment_decision_date,
            payable_amount, payable_currency, payment_date, status, revision
        ) VALUES (
            @incident, @notification, @country, @claimant, @insurer,
            @claimed, @claimedCurrency, @reference, @decision,
            @payable, @payableCurrency, @paid, @status, @revision
        )
        """
        |> withDateOperands

    let updateCase =
        """
        UPDATE claimcore.cases SET incident_date = @incident,
            incident_notification_date = @notification, incident_country = @country,
            claimant_name = @claimant, insurer_name = @insurer,
            claimed_amount = @claimed, claimed_currency = @claimedCurrency,
            payment_decision_date = @decision, payable_amount = @payable,
            payable_currency = @payableCurrency, payment_date = @paid, status = @status,
            revision = @revision
        WHERE case_reference = @reference AND revision = @expected
        """
        |> withDateOperands

    let insertChange =
        $"""
        INSERT INTO claimcore.case_changes (
            operation_id, case_reference, revision, command_name, request_sha256,
            request_format_version, snapshot_version, snapshot
        ) VALUES (
            @operation, @reference, @revision, @command, @fingerprint,
            {RecordVersions.RequestFingerprint}, {RecordVersions.Snapshot}, @snapshot
        )
        RETURNING recorded_at, recorded_by
        """

    let add (command: NpgsqlCommand) (name: string) (kind: NpgsqlDbType) (value: objnull) =
        let parameter = command.Parameters.Add(name, kind)
        parameter.Value <- value

    let text command name (value: string) =
        add command name NpgsqlDbType.Text (box value)

    let integer command name (value: int64) =
        add command name NpgsqlDbType.Bigint (box value)

    let uuid command name (value: Guid) =
        add command name NpgsqlDbType.Uuid (box value)

    let optional command name kind value =
        add
            command
            name
            kind
            (match value with
             | Some item -> box item
             | None -> box DBNull.Value)

    let lockKey (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) key =
        use command =
            new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
                connection,
                transaction
            )

        text command "key" key
        command.ExecuteNonQuery() |> ignore

    let lockKeyAsync (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) key =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))",
                    connection,
                    transaction
                )

            text command "key" key
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }
