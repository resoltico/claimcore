namespace ClaimCore.Contracts

open System
open System.Text.Json
open ClaimCore.Application

module internal WebWireQueries =
    let outcome (writer: Utf8JsonWriter) (tag: string) (write: unit -> unit) =
        writer.WriteStartObject()
        writer.WriteString("tag", tag)
        writer.WritePropertyName("data")
        write ()
        writer.WriteEndObject()

    let session (writer: Utf8JsonWriter) (authenticated: bool) (antiforgeryToken: string option) =
        outcome writer "SNAPSHOT" (fun () ->
            writer.WriteStartObject()
            writer.WriteBoolean("authenticated", authenticated)

            match antiforgeryToken with
            | Some value -> writer.WriteString("antiforgeryToken", value)
            | None -> writer.WriteNull("antiforgeryToken")

            writer.WriteEndObject())

    let description (writer: Utf8JsonWriter) (value: CoreDescription) =
        outcome writer "DESCRIBED" (fun () ->
            writer.WriteStartObject()

            writer.WriteString(
                "semanticFingerprint",
                value.SemanticFingerprint |> SemanticCoreFingerprint.value
            )

            let projection = ContractProjection.create value.Contract

            writer.WriteString(
                "webFingerprint",
                ContractRenderers.webFingerprint projection |> WebWireContractFingerprint.value
            )

            writer.WritePropertyName("runtime")
            writer.WriteStartObject()
            writer.WriteString("productVersion", value.Runtime.ProductVersion)

            writer.WriteString(
                "effectiveBusinessDate",
                value.Runtime.EffectiveBusinessDate.ToString("O")
            )

            writer.WriteString("timeZoneId", value.Runtime.TimeZoneId)
            writer.WriteEndObject()
            writer.WritePropertyName("definition")
            WebWireValues.semanticDefinition writer value
            writer.WriteEndObject())

    let private query
        (writer: Utf8JsonWriter)
        (succeeded: 'value -> unit)
        (value: QueryOutcome<'value>)
        =
        match value with
        | QueryOutcome.Succeeded item -> outcome writer "SUCCEEDED" (fun () -> succeeded item)
        | QueryOutcome.Rejected rejection ->
            outcome writer "REJECTED" (fun () -> CliWireValues.rejection writer rejection)
        | QueryOutcome.Failed fault ->
            outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)
        | QueryOutcome.Cancelled -> outcome writer "CANCELLED" writer.WriteNullValue

    let private lookup
        (writer: Utf8JsonWriter)
        (identityName: string)
        (writeIdentity: 'identity -> unit)
        (foundName: string)
        (writeFound: 'found -> unit)
        (value: Lookup<'found, 'identity>)
        =
        writer.WriteStartObject()

        match value with
        | Lookup.Found found ->
            writer.WriteString("tag", "FOUND")
            writer.WritePropertyName(foundName)
            writeFound found
        | Lookup.NotFound identity ->
            writer.WriteString("tag", "NOT_FOUND")
            writer.WritePropertyName(identityName)
            writeIdentity identity

        writer.WriteEndObject()

    let currentCase (writer: Utf8JsonWriter) (value: QueryOutcome<Lookup<CurrentCase, string>>) =
        query
            writer
            (lookup
                writer
                "caseReference"
                (fun (identity: string) -> writer.WriteStringValue(identity))
                "current"
                (CliWireValues.currentCase writer))
            value

    let casePage (writer: Utf8JsonWriter) (value: QueryOutcome<CaseSummaryPage>) =
        query
            writer
            (fun page ->
                writer.WriteStartObject()
                writer.WritePropertyName("items")
                writer.WriteStartArray()
                page.Items |> List.iter (CliWireValues.summary writer)
                writer.WriteEndArray()

                match page.NextAfterReference with
                | Some cursor -> writer.WriteString("nextCursor", cursor)
                | None -> writer.WriteNull("nextCursor")

                writer.WriteEndObject())
            value

    let history (writer: Utf8JsonWriter) (value: QueryOutcome<Lookup<HistoryResultPage, string>>) =
        let found =
            function
            | Lookup.Found(page: HistoryResultPage) ->
                writer.WriteStartObject()
                writer.WriteString("tag", "FOUND")
                writer.WritePropertyName("entries")
                writer.WriteStartArray()
                page.Entries |> List.iter (WebWireValues.historyEntry writer)
                writer.WriteEndArray()

                match page.NextCursor with
                | Some cursor -> writer.WriteString("nextCursor", cursor)
                | None -> writer.WriteNull("nextCursor")

                writer.WriteEndObject()
            | Lookup.NotFound(reference: string) ->
                writer.WriteStartObject()
                writer.WriteString("tag", "NOT_FOUND")
                writer.WriteString("caseReference", reference)
                writer.WriteEndObject()

        query writer found value

    let operation writer value =
        query
            writer
            (lookup
                writer
                "operationId"
                (fun (identity: Guid) -> writer.WriteStringValue(identity))
                "receipt"
                (WebWireValues.receipt writer))
            value

    let private recoveryQuery
        (writer: Utf8JsonWriter)
        (succeeded: 'value -> unit)
        (value: RecoveryQueryOutcome<'value>)
        =
        match value with
        | RecoveryQueryOutcome.RecoverySucceeded item ->
            outcome writer "SUCCEEDED" (fun () -> succeeded item)
        | RecoveryQueryOutcome.RecoveryRejected rejection ->
            outcome writer "REJECTED" (fun () -> CliWireValues.recoveryRejection writer rejection)
        | RecoveryQueryOutcome.RecoveryFailed fault ->
            outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)
        | RecoveryQueryOutcome.RecoveryCancelled -> outcome writer "CANCELLED" writer.WriteNullValue

    let recoveryPage (writer: Utf8JsonWriter) (value: RecoveryQueryOutcome<RecoveryPage>) =
        let item =
            function
            | RetainedRecoveryItem summary ->
                writer.WriteStartObject()
                writer.WriteString("tag", "RETAINED")
                writer.WritePropertyName("summary")
                CliWireValues.preparationSummary writer summary
                writer.WriteEndObject()
            | RevokedRecoveryItem revoked ->
                writer.WriteStartObject()
                writer.WriteString("tag", "REVOKED")
                writer.WritePropertyName("revocation")
                CliWireValues.revokedOperation writer revoked
                writer.WriteEndObject()

        recoveryQuery
            writer
            (fun (page: RecoveryPage) ->
                writer.WriteStartObject()
                writer.WriteString("view", WireTokens.recoveryListView page.View)
                writer.WritePropertyName("items")
                writer.WriteStartArray()
                page.Items |> List.iter item
                writer.WriteEndArray()

                match page.NextCursor with
                | Some cursor -> writer.WriteString("nextCursor", cursor)
                | None -> writer.WriteNull("nextCursor")

                writer.WriteNumber("pendingPreparationCount", page.PendingPreparationCount)

                writer.WriteNumber(
                    "pendingCanonicalRequestBytes",
                    page.PendingCanonicalRequestBytes
                )

                writer.WriteNumber("maximumPendingPreparations", page.MaximumPendingPreparations)

                writer.WriteNumber(
                    "maximumPendingCanonicalRequestBytes",
                    page.MaximumPendingCanonicalRequestBytes
                )

                writer.WriteBoolean("nearCapacity", page.NearCapacity)
                writer.WriteEndObject())
            value

    let private observation (writer: Utf8JsonWriter) value =
        lookup
            writer
            "identity"
            (fun (identity: Guid) -> writer.WriteStringValue(identity))
            "value"
            (WebWireValues.receipt writer)
            value

    let recoveryDetails
        (writer: Utf8JsonWriter)
        (value: RecoveryQueryOutcome<Lookup<RecoveryInspection, System.Guid>>)
        =
        let retained (found: RecoveryDetails) =
            writer.WriteStartObject()
            writer.WriteString("tag", "RETAINED")
            writer.WritePropertyName("value")
            writer.WriteStartObject()
            writer.WritePropertyName("preparation")
            WebWireValues.preparationDetails writer found.Preparation
            writer.WritePropertyName("observation")
            observation writer found.Observation
            writer.WriteEndObject()
            writer.WriteEndObject()

        let inspection =
            function
            | RetainedInspection details -> retained details
            | RevokedInspection revoked ->
                writer.WriteStartObject()
                writer.WriteString("tag", "REVOKED")
                writer.WritePropertyName("revocation")
                CliWireValues.revokedOperation writer revoked
                writer.WriteEndObject()

        recoveryQuery
            writer
            (lookup
                writer
                "identity"
                (fun (identity: Guid) -> writer.WriteStringValue(identity))
                "value"
                inspection)
            value

    let importPreview
        (writer: Utf8JsonWriter)
        (value: RecoveryQueryOutcome<RecoveryImportPreview>)
        =
        recoveryQuery writer (WebWireValues.importPreview writer) value

    let recoveryExport
        (writer: Utf8JsonWriter)
        (value: RecoveryQueryOutcome<Lookup<RecoveryExport, System.Guid>>)
        =
        match value with
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound identity) ->
            outcome writer "NOT_FOUND" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("operationId", identity)
                writer.WriteEndObject())
        | RecoveryQueryOutcome.RecoveryRejected rejection ->
            outcome writer "REJECTED" (fun () -> CliWireValues.recoveryRejection writer rejection)
        | RecoveryQueryOutcome.RecoveryFailed fault ->
            outcome writer "FAILED" (fun () -> CliWireValues.fault writer fault)
        | RecoveryQueryOutcome.RecoveryCancelled -> outcome writer "CANCELLED" writer.WriteNullValue
        | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found _) ->
            invalidArg (nameof value) "A found recovery export is a binary response."
