namespace ClaimCore.Contracts

open ClaimCore.ClientProtocol

open System
open System.Globalization
open System.Text.Json.Nodes
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat

module internal WireJson =
    let text (value: string) : JsonNode =
        (JsonValue.Create value |> nonNull) :> JsonNode

    let boolean value : JsonNode =
        (JsonValue.Create<bool> value |> nonNull) :> JsonNode

    let number value : JsonNode =
        (JsonValue.Create<int> value |> nonNull) :> JsonNode

    let revision value : JsonNode =
        (JsonValue.Create<int64> value |> nonNull) :> JsonNode

    let obj (properties: (string * (JsonNode | null)) list) : JsonNode =
        let result = JsonObject()
        properties |> List.iter (fun (key, value) -> result.Add(key, value))
        result

    let array values : JsonNode =
        let result = JsonArray()
        values |> List.iter (fun (value: JsonNode) -> result.Add value)
        result

    let optional (write: 'value -> JsonNode) value : JsonNode | null =
        match value with
        | Some value -> write value
        | None -> null

    let uuid (value: Guid) = text (value.ToString("D"))

    let date (value: DateOnly) =
        text (value.ToString("O", CultureInfo.InvariantCulture))

    let timestamp (value: DateTimeOffset) =
        text (value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))

    let tagged tag data = obj [ "tag", text tag; "data", data ]

module internal WireValues =
    open WireJson

    let disposition =
        function
        | Disposition.Active -> text "ACTIVE"
        | Disposition.Voided -> text "VOIDED"

    let acts values =
        values |> List.map (CaseActKinds.token >> text) |> array

    let private fields value =
        FieldDefinitions.values value
        |> List.map (fun (key, value) -> key, optional text value)
        |> obj

    let private state available =
        function
        | CaseView.Erased -> obj [ "tag", text "ERASED" ]
        | CaseView.Live(value, status) ->
            obj (
                [
                    "tag", text "LIVE"
                    "disposition", disposition status
                    "fields", fields value
                ]
                @ (available
                   |> Option.map (fun value -> [ "availableActs", acts value ])
                   |> Option.defaultValue [])
            )

    let snapshot (value: CaseSnapshot) =
        obj
            [
                "caseReference", text value.Reference
                "revision", revision value.Revision
                "state", state None value.View
            ]

    let private offers (values: OperationOffer list) =
        values
        |> List.map (fun value ->
            obj
                [
                    "definition", text value.Definition
                    "caseReference", optional text value.CaseReference
                    "expectedRevision", optional revision value.ExpectedRevision
                    "choices",
                    value.Choices
                    |> List.map (fun choice ->
                        obj
                            [
                                "input", text choice.Input
                                "values", choice.Values |> List.map text |> array
                            ])
                    |> array
                ])
        |> array

    let record (value: CaseReadModel) =
        obj
            [
                "caseReference", text value.Snapshot.Reference
                "revision", revision value.Snapshot.Revision
                "state", state (Some value.AvailableActs) value.Snapshot.View
                "offers", offers value.Offers
                "historyDetails", value.HistoryDetails |> List.map text |> array
            ]

    let position (value: LedgerPosition) =
        obj [ "sequence", revision value.Sequence; "entryHash", text value.EntryHash ]

    let private disclosure =
        function
        | SnapshotDisclosure.Disclosed view ->
            obj [ "tag", text "DISCLOSED"; "state", state None view ]
        | SnapshotDisclosure.Withheld -> obj [ "tag", text "WITHHELD" ]
        | SnapshotDisclosure.Erased -> obj [ "tag", text "ERASED" ]

    let receipt (value: OperationReceipt) =
        let scope =
            match value.Scope with
            | ReceiptScope.Case target ->
                [
                    "scope", text "CASE"
                    "caseReference", text target.Reference
                    "revision", revision target.Revision
                    "act", text (CaseActKinds.token target.Act)
                    "snapshot", disclosure target.Snapshot
                ]
            | ReceiptScope.Authority target ->
                [
                    "scope", text "AUTHORITY"
                    "targetActorId", uuid target.TargetActorId
                    "act", text (RecordedAuthorityActs.token target.Act)
                ]

        obj (
            [
                "operationId", uuid value.OperationId
                "position", position value.Position
                "recordedAt", timestamp value.RecordedAt
                "recordedBy", uuid value.RecordedBy
            ]
            @ scope
        )

    let summary =
        function
        | CaseSummary.Live(reference, value, status, disp) ->
            obj
                [
                    "state", text "LIVE"
                    "caseReference", text reference
                    "revision", revision value
                    "status", text (CaseStatuses.token status)
                    "disposition", disposition disp
                ]
        | CaseSummary.Erased(reference, value) ->
            obj
                [
                    "state", text "ERASED"
                    "caseReference", text reference
                    "revision", revision value
                ]

    let change (value: ChangeSummary) =
        obj
            [
                "operationId", uuid value.OperationId
                "position", position value.Position
                "revision", revision value.Revision
                "act", text (CaseActKinds.token value.Act)
                "recordedAt", timestamp value.RecordedAt
                "recordedBy", uuid value.RecordedBy
            ]

    let history =
        function
        | HistoryEntry.SummaryEntry value -> obj [ "tag", text "SUMMARY"; "change", change value ]
        | HistoryEntry.FullEntry value -> obj [ "tag", text "FULL"; "receipt", receipt value ]

    let context (value: RuntimeContext) =
        [
            "productVersion", text value.ProductVersion
            "businessDate", date value.EffectiveBusinessDate
            "timeZone", text value.TimeZoneId
        ]

    let actorContext (value: ActorContext) =
        obj (
            context value.Runtime
            @ [
                "capabilities", value.Capabilities |> List.map (Capabilities.token >> text) |> array
                "offers", offers value.Offers
            ]
        )

    let review (value: AdvisoryReview) =
        obj
            [
                "before", optional snapshot value.Before
                "after", snapshot value.Proposed
                "changes",
                (value.Changes
                 |> List.map (fun value ->
                     obj
                         [
                             "field", text value.FieldName
                             "before", optional text value.Before
                             "after", optional text value.After
                         ])
                 |> array)
                "context", obj (context value.Context)
            ]

    let private principal value =
        let value = PrincipalKey.describe value

        obj
            [
                "kind", text value.Kind
                "issuer", text value.Issuer
                "subject", text value.Subject
            ]

    let private scope =
        function
        | GrantTarget.Installation -> obj [ "kind", text "INSTALLATION" ]
        | GrantTarget.CaseReference reference ->
            obj [ "kind", text "CASE"; "caseReference", text reference ]

    let actor (value: ActorReview) =
        obj
            [
                "actorId", uuid value.ActorId
                "principal", principal value.Principal
                "enabled", boolean value.Enabled
                "grants",
                (value.Grants
                 |> List.map (fun grant ->
                     obj [ "role", text (Roles.token grant.Role); "scope", scope grant.Scope ])
                 |> array)
            ]

    let rejection (value: Rejection) =
        let diagnostic = RejectionDiagnostics.describe value

        obj
            [
                "diagnostic",
                obj
                    [
                        "id",
                        text (
                            RejectionDiagnostics.identifier diagnostic
                            |> RejectionDiagnosticIds.token
                        )
                        "parameters",
                        RejectionDiagnostics.values diagnostic
                        |> List.map (fun (key, value) -> key, number value)
                        |> obj
                    ]
                "message", text (RejectionPresentation.render value)
                "field", optional text value.Field
                "actualRevision", optional revision value.ActualRevision
                "recommendedAction", text (WireTokens.action value.Action)
            ]

    let fault (value: CoreFault) =
        obj
            [
                "diagnostic", obj [ "id", text (CoreFaults.token value); "parameters", obj [] ]
                "message", text (CoreFaultPresentation.render value)
                "recommendedAction", text (WireTokens.action value.Action)
            ]
