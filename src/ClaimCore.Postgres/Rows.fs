namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Globalization
open System.IO
open System.Text
open Npgsql
open NpgsqlTypes
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat

module internal Rows =
    let private ordinal (reader: DbDataReader) name = reader.GetOrdinal(name)
    let private text (reader: DbDataReader) (name: string) = reader.GetString(ordinal reader name)

    let private date (reader: DbDataReader) (name: string) =
        reader.GetInt32(ordinal reader name) |> ScalarEncoding.dateText

    let private optional (reader: DbDataReader) (name: string) read =
        if reader.IsDBNull(ordinal reader name) then
            None
        else
            Some(read reader name)

    let private restore snapshot =
        match Claim.restore snapshot with
        | Ok claim -> claim
        | Error _ -> raise (InvalidDataException("Stored case invariants failed."))

    let claim (reader: DbDataReader) =
        if reader.IsDBNull(ordinal reader "revision") then
            raise (InvalidDataException("The case has no concurrency record."))

        let status =
            match text reader "status" |> CaseStatuses.tryParse with
            | Some value -> value
            | None -> raise (InvalidDataException("Unknown stored status."))

        restore
            {
                Fields =
                    {
                        IncidentDate = date reader "incident_date"
                        IncidentNotificationDate = date reader "incident_notification_date"
                        IncidentCountry = text reader "incident_country"
                        ClaimantName = text reader "claimant_name"
                        InsurerName = text reader "insurer_name"
                        ClaimedAmount = text reader "claimed_amount"
                        ClaimedCurrency = text reader "claimed_currency"
                        CaseReference = text reader "case_reference"
                        PaymentDecisionDate = optional reader "payment_decision_date" date
                        PayableAmount = optional reader "payable_amount" text
                        PayableCurrency = optional reader "payable_currency" text
                        PaymentDate = optional reader "payment_date" date
                        Status = status
                    }
                Version = reader.GetInt64(ordinal reader "revision")
            }

    let receipt (reader: DbDataReader) replayed =
        if
            reader.GetInt16(ordinal reader "request_format_version")
            <> int16 RecordVersions.RequestFingerprint
            || reader.GetInt16(ordinal reader "snapshot_version")
               <> int16 RecordVersions.Snapshot
        then
            raise (InvalidDataException("Unsupported stored request or snapshot format version."))

        let snapshot =
            text reader "snapshot" |> Encoding.UTF8.GetBytes |> CaseRecord.decodeSnapshot

        let claim =
            match snapshot with
            | Ok value -> restore value
            | Error _ -> raise (InvalidDataException("Stored snapshot encoding failed."))

        let identity = Claim.view claim

        if
            identity.Fields.CaseReference <> text reader "case_reference"
            || identity.Version <> reader.GetInt64(ordinal reader "revision")
        then
            raise (InvalidDataException("Stored snapshot identity disagrees with its receipt."))

        let commandName = text reader "command_name"

        if
            not (
                CommandKinds.all
                |> List.exists (fun kind -> CommandKinds.token kind = commandName)
            )
        then
            raise (InvalidDataException("Unknown stored operation kind."))

        {
            OperationId = reader.GetGuid(ordinal reader "operation_id")
            Case = claim
            RecordedAt = reader.GetFieldValue<DateTimeOffset>(ordinal reader "recorded_at")
            RecordedBy = text reader "recorded_by"
            Replayed = replayed
            CommandName = commandName
        }

    let bindClaim (command: NpgsqlCommand) claim =
        let fields = (Claim.view claim).Fields

        let parseAmount (value: string) =
            Decimal.Parse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)

        Sql.text command "reference" fields.CaseReference

        Sql.add
            command
            "incident"
            NpgsqlDbType.Integer
            (box (ScalarEncoding.dateDays fields.IncidentDate))

        Sql.add
            command
            "notification"
            NpgsqlDbType.Integer
            (box (ScalarEncoding.dateDays fields.IncidentNotificationDate))

        Sql.text command "country" fields.IncidentCountry
        Sql.text command "claimant" fields.ClaimantName
        Sql.text command "insurer" fields.InsurerName
        Sql.add command "claimed" NpgsqlDbType.Numeric (box (parseAmount fields.ClaimedAmount))
        Sql.text command "claimedCurrency" fields.ClaimedCurrency

        Sql.optional
            command
            "decision"
            NpgsqlDbType.Integer
            (fields.PaymentDecisionDate |> Option.map ScalarEncoding.dateDays)

        Sql.optional
            command
            "payable"
            NpgsqlDbType.Numeric
            (fields.PayableAmount |> Option.map parseAmount)

        Sql.optional command "payableCurrency" NpgsqlDbType.Text fields.PayableCurrency

        Sql.optional
            command
            "paid"
            NpgsqlDbType.Integer
            (fields.PaymentDate |> Option.map ScalarEncoding.dateDays)

        Sql.text command "status" (CaseStatuses.token fields.Status)
