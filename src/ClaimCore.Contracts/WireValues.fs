namespace ClaimCore.Contracts

open System
open System.Globalization
open System.Text.Json.Nodes
open ClaimCore.Application
open ClaimCore.Domain

module internal WireJson =
    let text (value: string) : JsonNode =
        (JsonValue.Create value |> nonNull) :> JsonNode

    let boolean value : JsonNode =
        (JsonValue.Create<bool> value |> nonNull) :> JsonNode

    let number value : JsonNode =
        (JsonValue.Create<int> value |> nonNull) :> JsonNode

    let obj (properties: (string * (JsonNode | null)) list) : JsonNode =
        let result = JsonObject()

        properties
        |> List.iter (fun (key: string, value: JsonNode | null) -> result.Add(key, value))

        result

    let array values : JsonNode =
        let result = JsonArray()
        values |> List.iter (fun (value: JsonNode) -> result.Add(value))
        result

    let optional (write: 'value -> JsonNode) value : JsonNode | null =
        match value with
        | Some item -> write item
        | None -> null

    let revision (value: int64) =
        value.ToString(CultureInfo.InvariantCulture) |> text

    let uuid (value: Guid) = value.ToString("D") |> text

    let date (value: DateOnly) =
        value.ToString("O", CultureInfo.InvariantCulture) |> text

    let timestamp (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) |> text

    let tagged tag data = obj [ "tag", text tag; "data", data ]

module internal WireValues =
    open WireJson

    let disposition =
        function
        | CaseDisposition.Active -> text "ACTIVE"
        | CaseDisposition.Voided -> text "VOIDED"

    let act =
        function
        | AcceptedAct.Command kind -> CommandKinds.token kind |> text
        | AcceptedAct.Stewardship kind ->
            (match kind with
             | StewardshipKind.Void -> "VOID"
             | StewardshipKind.Reinstate -> "REINSTATE"
             | StewardshipKind.Erase -> "ERASE")
            |> text

    let commands values =
        values |> List.map (CommandKinds.token >> text) |> array

    let caseView (value: CaseView) =
        obj
            [
                "fields",
                (FieldDefinitions.values value.Fields
                 |> List.map (fun (name, value) -> name, optional text value)
                 |> obj)
                "revision", revision value.Version
            ]

    let position (value: LedgerPosition) =
        obj [ "sequence", revision value.Sequence; "entryHash", text value.EntryHash ]

    let receipt (value: OperationReceipt) =
        obj
            [
                "operationId", uuid value.OperationId
                "position", position value.Position
                "caseReference", text value.CaseReference
                "revision", revision value.Revision
                "act", act value.Act
                "disposition", optional disposition value.Disposition
                "snapshot", optional caseView value.Snapshot
                "recordedAt", timestamp value.RecordedAt
                "recordedBy", uuid value.RecordedBy
            ]

    let record =
        function
        | CaseRecord.Live(view, state, available) ->
            obj
                [
                    "state", text "LIVE"
                    "case", caseView view
                    "disposition", disposition state
                    "availableCommands", commands available
                ]
        | CaseRecord.Erased(reference, version) ->
            obj
                [
                    "state", text "ERASED"
                    "caseReference", text reference
                    "revision", revision version
                ]

    let summary =
        function
        | CaseSummary.Live(reference, version, status, state) ->
            obj
                [
                    "state", text "LIVE"
                    "caseReference", text reference
                    "revision", revision version
                    "status", text (CaseStatuses.token status)
                    "disposition", disposition state
                ]
        | CaseSummary.Erased(reference, version) ->
            obj
                [
                    "state", text "ERASED"
                    "caseReference", text reference
                    "revision", revision version
                ]

    let change (value: ChangeSummary) =
        obj
            [
                "operationId", uuid value.OperationId
                "position", position value.Position
                "revision", revision value.Revision
                "act", act value.Act
                "recordedAt", timestamp value.RecordedAt
                "recordedBy", uuid value.RecordedBy
            ]

    let history =
        function
        | HistoryEntry.SummaryEntry value -> obj [ "tag", text "SUMMARY"; "change", change value ]
        | HistoryEntry.FullEntry value -> obj [ "tag", text "FULL"; "receipt", receipt value ]

    let context (value: RuntimeContext) =
        obj
            [
                "productVersion", text value.ProductVersion
                "effectiveBusinessDate", date value.EffectiveBusinessDate
                "timeZoneId", text value.TimeZoneId
            ]

    let review (value: AdvisoryReview) =
        obj
            [
                "before", optional caseView value.Before
                "proposed", caseView value.Proposed
                "changes",
                (value.Changes
                 |> List.map (fun field ->
                     obj
                         [
                             "fieldName", text field.FieldName
                             "before", optional text field.Before
                             "after", optional text field.After
                         ])
                 |> array)
                "context", context value.Context
                "advisory", boolean value.IsAdvisory
            ]

    let rejection (value: Rejection) =
        let diagnostic = RejectionDiagnostics.describe value

        obj
            [
                "code", text (WireTokens.rejectionCode value.Code)
                "diagnostic",
                obj
                    [
                        "id",
                        text (
                            RejectionDiagnostics.identifier diagnostic
                            |> RejectionDiagnosticIds.token
                        )
                        "parameters",
                        (RejectionDiagnostics.values diagnostic
                         |> List.map (fun (name, value) -> name, number value)
                         |> obj)
                    ]
                "message", text (RejectionPresentation.render value)
                "field", optional text value.Field
                "actualRevision", optional revision value.ActualVersion
                "recommendedAction", text (WireTokens.action value.Action)
            ]

    let fault value =
        let message = CoreFaultPresentation.render value

        obj
            [
                "code", text (WireTokens.faultCode value.Code)
                "diagnostic", obj [ "id", text (CoreFaults.token value); "parameters", obj [] ]
                "message", text message
                "recommendedAction", text (WireTokens.action value.Action)
            ]
