// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal CaseGetResponseOutcomeSucceededDataJson =
    let read (value: JsonElement) : CaseGetResponseOutcomeSucceededData =
        match JsonRead.tag "tag" value with
        | "FOUND" ->
            CaseGetResponseOutcomeSucceededData.Found(
                (CaseGetResponseOutcomeSucceededDataFoundJson.read) value
            )
        | "NOT_FOUND" ->
            CaseGetResponseOutcomeSucceededData.NotFound(
                (CaseGetResponseOutcomeSucceededDataNotFoundJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: CaseGetResponseOutcomeSucceededData) =
        match value with
        | CaseGetResponseOutcomeSucceededData.Found item ->
            (CaseGetResponseOutcomeSucceededDataFoundJson.write) writer item
        | CaseGetResponseOutcomeSucceededData.NotFound item ->
            (CaseGetResponseOutcomeSucceededDataNotFoundJson.write) writer item

module internal CaseGetResponseOutcomeSucceededJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CaseGetResponseOutcomeSucceeded =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar55.read) value
            Data = JsonRead.required "data" (CaseGetResponseOutcomeSucceededDataJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseGetResponseOutcomeSucceeded) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar55.write) writer value.Tag
        JsonWrite.property "data" (CaseGetResponseOutcomeSucceededDataJson.write) writer value.Data
        writer.WriteEndObject()

module internal CaseGetResponseOutcomeRejectedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CaseGetResponseOutcomeRejected =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar16.read) value
            Data = JsonRead.required "data" (RejectionJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseGetResponseOutcomeRejected) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar16.write) writer value.Tag
        JsonWrite.property "data" (RejectionJson.write) writer value.Data
        writer.WriteEndObject()

module internal CaseGetResponseOutcomeFailedJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CaseGetResponseOutcomeFailed =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar56.read) value
            Data = JsonRead.required "data" (FaultJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseGetResponseOutcomeFailed) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar56.write) writer value.Tag
        JsonWrite.property "data" (FaultJson.write) writer value.Data
        writer.WriteEndObject()

module internal CaseGetResponseOutcomeCancelledJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CaseGetResponseOutcomeCancelled =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar57.read) value
            Data = JsonRead.required "data" (ProtocolScalar58.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseGetResponseOutcomeCancelled) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar57.write) writer value.Tag
        JsonWrite.property "data" (ProtocolScalar58.write) writer value.Data
        writer.WriteEndObject()

module internal CaseGetResponseOutcomeJson =
    let read (value: JsonElement) : CaseGetResponseOutcome =
        match JsonRead.tag "tag" value with
        | "SUCCEEDED" ->
            CaseGetResponseOutcome.Succeeded((CaseGetResponseOutcomeSucceededJson.read) value)
        | "REJECTED" ->
            CaseGetResponseOutcome.Rejected((CaseGetResponseOutcomeRejectedJson.read) value)
        | "FAILED" -> CaseGetResponseOutcome.Failed((CaseGetResponseOutcomeFailedJson.read) value)
        | "CANCELLED" ->
            CaseGetResponseOutcome.Cancelled((CaseGetResponseOutcomeCancelledJson.read) value)
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: CaseGetResponseOutcome) =
        match value with
        | CaseGetResponseOutcome.Succeeded item ->
            (CaseGetResponseOutcomeSucceededJson.write) writer item
        | CaseGetResponseOutcome.Rejected item ->
            (CaseGetResponseOutcomeRejectedJson.write) writer item
        | CaseGetResponseOutcome.Failed item -> (CaseGetResponseOutcomeFailedJson.write) writer item
        | CaseGetResponseOutcome.Cancelled item ->
            (CaseGetResponseOutcomeCancelledJson.write) writer item

module internal CaseGetResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : CaseGetResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar54.read) value
            Outcome = JsonRead.required "outcome" (CaseGetResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseGetResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar54.write) writer value.Endpoint
        JsonWrite.property "outcome" (CaseGetResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal CaseGetRequestJson =
    let private properties = [ "caseReference" ]

    let read (value: JsonElement) : CaseGetRequest =
        JsonRead.objectValue properties value

        {
            CaseReference = JsonRead.required "caseReference" (ProtocolScalar5.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseGetRequest) =
        writer.WriteStartObject()
        JsonWrite.property "caseReference" (ProtocolScalar5.write) writer value.CaseReference
        writer.WriteEndObject()

module internal CaseListResponseOutcomeSucceededDataJson =
    let private properties = [ "items"; "nextCursor" ]

    let read (value: JsonElement) : CaseListResponseOutcomeSucceededData =
        JsonRead.objectValue properties value

        {
            Items =
                JsonRead.required "items" (JsonRead.array None None (CaseSummaryJson.read)) value
            NextCursor =
                JsonRead.required "nextCursor" (JsonRead.nullable (ProtocolScalar8.read)) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseListResponseOutcomeSucceededData) =
        writer.WriteStartObject()
        JsonWrite.property "items" (JsonWrite.array (CaseSummaryJson.write)) writer value.Items

        JsonWrite.property
            "nextCursor"
            (JsonWrite.nullable (ProtocolScalar8.write))
            writer
            value.NextCursor

        writer.WriteEndObject()

module internal CaseListResponseOutcomeSucceededJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CaseListResponseOutcomeSucceeded =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar55.read) value
            Data = JsonRead.required "data" (CaseListResponseOutcomeSucceededDataJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseListResponseOutcomeSucceeded) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar55.write) writer value.Tag
        JsonWrite.property "data" (CaseListResponseOutcomeSucceededDataJson.write) writer value.Data
        writer.WriteEndObject()

module internal CaseListResponseOutcomeJson =
    let read (value: JsonElement) : CaseListResponseOutcome =
        match JsonRead.tag "tag" value with
        | "SUCCEEDED" ->
            CaseListResponseOutcome.Succeeded((CaseListResponseOutcomeSucceededJson.read) value)
        | "REJECTED" ->
            CaseListResponseOutcome.Rejected((CaseGetResponseOutcomeRejectedJson.read) value)
        | "FAILED" -> CaseListResponseOutcome.Failed((CaseGetResponseOutcomeFailedJson.read) value)
        | "CANCELLED" ->
            CaseListResponseOutcome.Cancelled((CaseGetResponseOutcomeCancelledJson.read) value)
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: CaseListResponseOutcome) =
        match value with
        | CaseListResponseOutcome.Succeeded item ->
            (CaseListResponseOutcomeSucceededJson.write) writer item
        | CaseListResponseOutcome.Rejected item ->
            (CaseGetResponseOutcomeRejectedJson.write) writer item
        | CaseListResponseOutcome.Failed item ->
            (CaseGetResponseOutcomeFailedJson.write) writer item
        | CaseListResponseOutcome.Cancelled item ->
            (CaseGetResponseOutcomeCancelledJson.write) writer item

module internal CaseListResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : CaseListResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar59.read) value
            Outcome = JsonRead.required "outcome" (CaseListResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseListResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar59.write) writer value.Endpoint
        JsonWrite.property "outcome" (CaseListResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal CaseListRequestJson =
    let private properties = [ "cursor"; "limit" ]

    let read (value: JsonElement) : CaseListRequest =
        JsonRead.objectValue properties value

        {
            Cursor = JsonRead.optional "cursor" (ProtocolScalar8.read) value
            Limit = JsonRead.required "limit" (ProtocolScalar60.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseListRequest) =
        writer.WriteStartObject()
        JsonWrite.optional "cursor" (ProtocolScalar8.write) writer value.Cursor
        JsonWrite.property "limit" (ProtocolScalar60.write) writer value.Limit
        writer.WriteEndObject()

module internal CaseHistoryResponseOutcomeSucceededDataFoundJson =
    let private properties = [ "tag"; "entries"; "nextCursor" ]

    let read (value: JsonElement) : CaseHistoryResponseOutcomeSucceededDataFound =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar44.read) value
            Entries =
                JsonRead.required "entries" (JsonRead.array None None (HistoryEntryJson.read)) value
            NextCursor =
                JsonRead.required "nextCursor" (JsonRead.nullable (ProtocolScalar8.read)) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseHistoryResponseOutcomeSucceededDataFound) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar44.write) writer value.Tag
        JsonWrite.property "entries" (JsonWrite.array (HistoryEntryJson.write)) writer value.Entries

        JsonWrite.property
            "nextCursor"
            (JsonWrite.nullable (ProtocolScalar8.write))
            writer
            value.NextCursor

        writer.WriteEndObject()
