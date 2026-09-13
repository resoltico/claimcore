namespace ClaimCore.Contracts

open System
open System.Globalization
open System.Text.Json
open ClaimCore.Application

module internal CliWireQueries =
    let cancelled (writer: Utf8JsonWriter) =
        writer.WriteStartObject()
        writer.WriteString("kind", "cancelled")
        writer.WriteEndObject()

    let rejected (writer: Utf8JsonWriter) (rejection: Rejection) =
        writer.WriteStartObject()
        writer.WriteString("kind", "rejected")
        writer.WritePropertyName("rejection")
        CliWireValues.rejection writer rejection
        writer.WriteEndObject()

    let failed (writer: Utf8JsonWriter) (fault: CoreFault) =
        writer.WriteStartObject()
        writer.WriteString("kind", "failed")
        writer.WritePropertyName("fault")
        CliWireValues.fault writer fault
        writer.WriteEndObject()

    let query
        (writer: Utf8JsonWriter)
        (success: Utf8JsonWriter -> 'value -> unit)
        (outcome: QueryOutcome<'value>)
        =
        match outcome with
        | QueryOutcome.Succeeded value -> success writer value
        | QueryOutcome.Rejected value -> rejected writer value
        | QueryOutcome.Failed value -> failed writer value
        | QueryOutcome.Cancelled -> cancelled writer

    let private lookup
        (writer: Utf8JsonWriter)
        (found: Utf8JsonWriter -> 'value -> unit)
        (missing: Utf8JsonWriter -> 'identity -> unit)
        (value: Lookup<'value, 'identity>)
        =
        match value with
        | Lookup.Found value -> found writer value
        | Lookup.NotFound identity ->
            writer.WriteStartObject()
            writer.WriteString("kind", "notFound")
            missing writer identity
            writer.WriteEndObject()

    let currentCase (writer: Utf8JsonWriter) (value: QueryOutcome<Lookup<CurrentCase, string>>) =
        let found (output: Utf8JsonWriter) (item: CurrentCase) =
            output.WriteStartObject()
            output.WriteString("kind", "found")
            output.WritePropertyName("value")
            CliWireValues.currentCase output item
            output.WriteEndObject()

        let missing (output: Utf8JsonWriter) (reference: string) =
            output.WriteString("caseReference", reference)

        query writer (fun output -> lookup output found missing) value

    let casePage (writer: Utf8JsonWriter) (value: QueryOutcome<CaseSummaryPage>) =
        let success (output: Utf8JsonWriter) (page: CaseSummaryPage) =
            output.WriteStartObject()
            output.WriteString("kind", "succeeded")
            output.WriteStartArray("items")
            page.Items |> List.iter (CliWireValues.summary output)
            output.WriteEndArray()

            match page.NextAfterReference with
            | Some cursor -> output.WriteString("nextCursor", cursor)
            | None -> output.WriteNull("nextCursor")

            output.WriteEndObject()

        query writer success value

    let private historyEntry (writer: Utf8JsonWriter) =
        function
        | HistoryEntry.SummaryEntry value ->
            writer.WriteStartObject()
            writer.WriteString("kind", "summary")
            writer.WriteString("operationId", value.OperationId)

            writer.WriteString("revision", value.Revision.ToString(CultureInfo.InvariantCulture))

            writer.WriteString("command", CliWireValues.command value.Command)
            writer.WriteString("recordedAt", value.RecordedAt.ToUniversalTime().ToString("O"))
            writer.WriteString("recordedBy", value.RecordedBy)
            writer.WriteEndObject()
        | HistoryEntry.FullEntry value ->
            writer.WriteStartObject()
            writer.WriteString("kind", "full")
            writer.WritePropertyName("receipt")
            CliWireValues.receipt true writer value
            writer.WriteEndObject()

    let history (writer: Utf8JsonWriter) (value: QueryOutcome<Lookup<HistoryResultPage, string>>) =
        let found (output: Utf8JsonWriter) (page: HistoryResultPage) =
            output.WriteStartObject()
            output.WriteString("kind", "found")
            output.WriteStartArray("entries")
            page.Entries |> List.iter (historyEntry output)
            output.WriteEndArray()

            match page.NextCursor with
            | Some cursor -> output.WriteString("nextCursor", cursor)
            | None -> output.WriteNull("nextCursor")

            output.WriteEndObject()

        let missing (output: Utf8JsonWriter) (reference: string) =
            output.WriteString("caseReference", reference)

        query writer (fun output -> lookup output found missing) value

    let operation (writer: Utf8JsonWriter) (value: QueryOutcome<Lookup<OperationReceipt, Guid>>) =
        let found (output: Utf8JsonWriter) (receipt: OperationReceipt) =
            output.WriteStartObject()
            output.WriteString("kind", "found")
            output.WritePropertyName("receipt")
            CliWireValues.receipt true output receipt
            output.WriteEndObject()

        let missing (output: Utf8JsonWriter) (operationId: Guid) =
            output.WriteString("operationId", operationId)

        query writer (fun output -> lookup output found missing) value

    let private recoveryQuery
        (writer: Utf8JsonWriter)
        (success: Utf8JsonWriter -> 'value -> unit)
        (outcome: RecoveryQueryOutcome<'value>)
        =
        match outcome with
        | RecoveryQueryOutcome.RecoverySucceeded value -> success writer value
        | RecoveryQueryOutcome.RecoveryRejected value ->
            writer.WriteStartObject()
            writer.WriteString("kind", "rejected")
            writer.WritePropertyName("rejection")
            CliWireValues.recoveryRejection writer value
            writer.WriteEndObject()
        | RecoveryQueryOutcome.RecoveryFailed value -> failed writer value
        | RecoveryQueryOutcome.RecoveryCancelled -> cancelled writer

    let recoveryPage (writer: Utf8JsonWriter) (value: RecoveryQueryOutcome<RecoveryPage>) =
        let success (output: Utf8JsonWriter) (page: RecoveryPage) =
            output.WriteStartObject()
            output.WriteString("kind", "succeeded")
            output.WriteStartArray("items")
            page.Items |> List.iter (CliWireValues.preparationSummary output)
            output.WriteEndArray()

            match page.NextCursor with
            | Some cursor -> output.WriteString("nextCursor", cursor)
            | None -> output.WriteNull("nextCursor")

            output.WriteEndObject()

        recoveryQuery writer success value

    let private observation (writer: Utf8JsonWriter) (value: Lookup<OperationReceipt, Guid>) =
        match value with
        | Lookup.Found receipt ->
            writer.WriteStartObject()
            writer.WriteString("kind", "found")
            writer.WritePropertyName("receipt")
            CliWireValues.receipt true writer receipt
            writer.WriteEndObject()
        | Lookup.NotFound operationId ->
            writer.WriteStartObject()
            writer.WriteString("kind", "notFound")
            writer.WriteString("operationId", operationId)
            writer.WriteEndObject()

    let recoveryDetails
        (writer: Utf8JsonWriter)
        (value: RecoveryQueryOutcome<Lookup<RecoveryDetails, Guid>>)
        =
        let found (output: Utf8JsonWriter) (details: RecoveryDetails) =
            output.WriteStartObject()
            output.WriteString("kind", "found")
            output.WritePropertyName("preparation")
            CliWireValues.preparationDetails output details.Preparation
            output.WritePropertyName("observation")
            observation output details.Observation
            output.WriteEndObject()

        let missing (output: Utf8JsonWriter) (operationId: Guid) =
            output.WriteString("operationId", operationId)

        recoveryQuery writer (fun output -> lookup output found missing) value

    let recoveryExport
        (operationId: Guid)
        (writer: Utf8JsonWriter)
        (value: RecoveryQueryOutcome<Lookup<RecoveryExport, Guid>>)
        =
        match value with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound missingOperationId) ->
            writer.WriteStartObject()
            writer.WriteString("kind", "notFound")
            writer.WriteString("operationId", missingOperationId)
            writer.WriteEndObject()
        | RecoveryQueryOutcome.RecoveryRejected rejection ->
            writer.WriteStartObject()
            writer.WriteString("kind", "rejected")
            writer.WritePropertyName("rejection")
            CliWireValues.recoveryRejection writer rejection
            writer.WriteEndObject()
        | RecoveryQueryOutcome.RecoveryFailed fault -> failed writer fault
        | RecoveryQueryOutcome.RecoveryCancelled -> cancelled writer
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) ->
            invalidArg
                (nameof value)
                ("A found export must be rendered through the private-file result for "
                 + operationId.ToString("D")
                 + ".")
