namespace ClaimCore.Contracts

open System
open System.Globalization
open System.Text.Json
open ClaimCore.Application
open ClaimCore.Domain

module internal CliWireValues =
    let private revision (value: int64) =
        value.ToString(CultureInfo.InvariantCulture)

    let command = WireTokens.command
    let action = WireTokens.action
    let rejectionCode = WireTokens.rejectionCode
    let faultCode = WireTokens.faultCode
    let recoveryRejectionCode = WireTokens.recoveryRejectionCode

    let private optional (writer: Utf8JsonWriter) (name: string) (value: string option) =
        match value with
        | Some value -> writer.WriteString(name, value)
        | None -> writer.WriteNull(name)

    let private diagnostic (writer: Utf8JsonWriter) (value: Rejection) =
        let projected = RejectionDiagnostics.describe value
        writer.WriteStartObject()

        writer.WriteString(
            "id",
            RejectionDiagnostics.identifier projected |> RejectionDiagnosticIds.token
        )

        writer.WritePropertyName("parameters")
        writer.WriteStartObject()

        RejectionDiagnostics.values projected
        |> List.iter (fun (name, value) -> writer.WriteNumber(name, value))

        writer.WriteEndObject()
        writer.WriteEndObject()

    let rejection (writer: Utf8JsonWriter) (value: Rejection) =
        writer.WriteStartObject()
        writer.WriteString("code", rejectionCode value.Code)
        writer.WritePropertyName("diagnostic")
        diagnostic writer value
        writer.WriteString("message", RejectionPresentation.render value)
        optional writer "field" value.Field
        optional writer "actualRevision" (value.ActualVersion |> Option.map revision)
        writer.WriteString("recommendedAction", action value.Action)
        writer.WriteEndObject()

    let private parameterlessDiagnostic (writer: Utf8JsonWriter) identifier =
        writer.WritePropertyName("diagnostic")
        writer.WriteStartObject()
        writer.WriteString("id", (identifier: string))
        writer.WritePropertyName("parameters")
        writer.WriteStartObject()
        writer.WriteEndObject()
        writer.WriteEndObject()

    let fault (writer: Utf8JsonWriter) (value: CoreFault) =
        writer.WriteStartObject()
        writer.WriteString("code", faultCode value.Code)
        parameterlessDiagnostic writer (CoreFaults.token value)
        writer.WriteString("message", CoreFaultPresentation.render value)
        writer.WriteString("recommendedAction", action value.Action)
        writer.WriteEndObject()

    let recoveryRejection (writer: Utf8JsonWriter) (value: RecoveryRejection) =
        writer.WriteStartObject()
        writer.WriteString("code", recoveryRejectionCode value.Code)
        parameterlessDiagnostic writer (RecoveryRejections.token value)
        writer.WriteString("message", RecoveryRejectionPresentation.render value)
        writer.WriteString("recommendedAction", action value.Action)
        writer.WriteEndObject()

    let localFault (writer: Utf8JsonWriter) (value: CliLocalFault) =
        writer.WriteStartObject()
        writer.WriteString("code", faultCode (CliLocalFaults.code value))
        parameterlessDiagnostic writer (CliLocalFaults.token value)
        writer.WriteString("message", CliLocalFaults.render value)
        writer.WriteString("recommendedAction", action RecommendedAction.StopAndInvestigate)
        writer.WriteEndObject()

    let caseView (writer: Utf8JsonWriter) (value: CaseView) =
        writer.WriteStartObject()
        writer.WritePropertyName("fields")
        writer.WriteStartObject()

        FieldDefinitions.values value.Fields
        |> List.iter (fun (name, field) -> optional writer name field)

        writer.WriteEndObject()
        writer.WriteString("revision", revision value.Version)
        writer.WriteEndObject()

    let currentCase (writer: Utf8JsonWriter) (value: CurrentCase) =
        writer.WriteStartObject()
        writer.WritePropertyName("case")
        caseView writer value.Record
        writer.WritePropertyName("availableCommands")
        writer.WriteStartArray()
        value.AvailableCommands |> List.iter (command >> writer.WriteStringValue)
        writer.WriteEndArray()
        writer.WriteEndObject()

    let receipt includeSnapshot (writer: Utf8JsonWriter) (value: OperationReceipt) =
        writer.WriteStartObject()
        writer.WriteString("operationId", value.OperationId)
        writer.WriteString("caseReference", value.Snapshot.Fields.CaseReference)
        writer.WriteString("revision", revision value.Snapshot.Version)
        writer.WriteString("command", command value.Command)
        writer.WriteString("recordedAt", value.RecordedAt.ToUniversalTime().ToString("O"))
        writer.WriteString("recordedBy", value.RecordedBy)
        writer.WriteBoolean("replayed", value.Replayed)

        if includeSnapshot then
            writer.WritePropertyName("snapshot")
            caseView writer value.Snapshot

        writer.WriteEndObject()

    let summary (writer: Utf8JsonWriter) (value: CaseSummary) =
        writer.WriteStartObject()
        writer.WriteString("caseReference", value.CaseReference)
        writer.WriteString("revision", revision value.Revision)
        writer.WriteString("status", CaseStatuses.token value.Status)
        writer.WriteEndObject()

    let preparationSummary (writer: Utf8JsonWriter) (value: PreparationSummary) =
        writer.WriteStartObject()
        writer.WriteString("operationId", value.OperationId)
        writer.WriteString("caseReference", value.CaseReference)
        writer.WriteString("command", command value.Command)
        writer.WriteString("preparedAt", value.PreparedAt.ToUniversalTime().ToString("O"))
        writer.WriteString("state", WireTokens.preparationState value.State)
        writer.WriteString("authority", WireTokens.recoveryAuthority value.Authority)
        optional writer "requestSha256" value.RequestSha256
        writer.WritePropertyName("availableActions")
        writer.WriteStartArray()

        value.AvailableActions
        |> List.iter (WireTokens.recoveryAction >> writer.WriteStringValue)

        writer.WriteEndArray()
        writer.WriteEndObject()

    let revokedOperation (writer: Utf8JsonWriter) (value: RevokedOperation) =
        writer.WriteStartObject()
        writer.WriteString("operationId", value.OperationId)
        writer.WriteString("revokedAt", value.RevokedAt.ToUniversalTime().ToString("O"))
        writer.WriteString("reason", value.Reason)
        writer.WriteEndObject()

    let private attempt (writer: Utf8JsonWriter) (value: PreparationAttempt) =
        writer.WriteStartObject()
        writer.WriteString("attemptId", value.AttemptId)
        writer.WriteString("startedAt", value.StartedAt.ToUniversalTime().ToString("O"))
        optional writer "settlement" value.Settlement

        optional
            writer
            "settledAt"
            (value.SettledAt |> Option.map (fun item -> item.ToUniversalTime().ToString("O")))

        writer.WriteEndObject()

    let private attemptPage (writer: Utf8JsonWriter) (value: PreparationAttemptPage) =
        writer.WriteStartObject()
        writer.WritePropertyName("items")
        writer.WriteStartArray()
        value.Items |> List.iter (attempt writer)
        writer.WriteEndArray()
        optional writer "nextCursor" value.NextCursor
        writer.WriteBoolean("legacyUncertainty", value.LegacyUncertainty)
        writer.WriteEndObject()

    let preparationDetails (writer: Utf8JsonWriter) (value: PreparationDetails) =
        writer.WriteStartObject()
        writer.WritePropertyName("summary")
        preparationSummary writer value.Summary
        writer.WriteString("expectedRevision", revision value.ExpectedVersion)
        writer.WritePropertyName("authoredValues")
        writer.WriteStartObject()

        value.AuthoredValues
        |> List.iter (fun (name, text) -> writer.WriteString(name, text))

        writer.WriteEndObject()
        writer.WriteNumber("canonicalCommandFormat", value.CanonicalCommandFormat)
        writer.WriteString("preparingApplicationVersion", value.PreparingApplicationVersion)
        writer.WriteString("preparingContractFingerprint", value.PreparingContractFingerprint)
        writer.WriteString("preparingContractKind", value.PreparingContractKind)
        writer.WritePropertyName("attempts")
        attemptPage writer value.Attempts
        writer.WriteEndObject()
