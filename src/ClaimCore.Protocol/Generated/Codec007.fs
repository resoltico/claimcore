// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal CaseHistoryResponseOutcomeSucceededDataJson =
    let read (value: JsonElement) : CaseHistoryResponseOutcomeSucceededData =
        match JsonRead.tag "tag" value with
        | "FOUND" ->
            CaseHistoryResponseOutcomeSucceededData.Found(
                (CaseHistoryResponseOutcomeSucceededDataFoundJson.read) value
            )
        | "NOT_FOUND" ->
            CaseHistoryResponseOutcomeSucceededData.NotFound(
                (CaseGetResponseOutcomeSucceededDataNotFoundJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: CaseHistoryResponseOutcomeSucceededData) =
        match value with
        | CaseHistoryResponseOutcomeSucceededData.Found item ->
            (CaseHistoryResponseOutcomeSucceededDataFoundJson.write) writer item
        | CaseHistoryResponseOutcomeSucceededData.NotFound item ->
            (CaseGetResponseOutcomeSucceededDataNotFoundJson.write) writer item

module internal CaseHistoryResponseOutcomeSucceededJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : CaseHistoryResponseOutcomeSucceeded =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar55.read) value
            Data = JsonRead.required "data" (CaseHistoryResponseOutcomeSucceededDataJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseHistoryResponseOutcomeSucceeded) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar55.write) writer value.Tag

        JsonWrite.property
            "data"
            (CaseHistoryResponseOutcomeSucceededDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal CaseHistoryResponseOutcomeJson =
    let read (value: JsonElement) : CaseHistoryResponseOutcome =
        match JsonRead.tag "tag" value with
        | "SUCCEEDED" ->
            CaseHistoryResponseOutcome.Succeeded(
                (CaseHistoryResponseOutcomeSucceededJson.read) value
            )
        | "REJECTED" ->
            CaseHistoryResponseOutcome.Rejected((CaseGetResponseOutcomeRejectedJson.read) value)
        | "FAILED" ->
            CaseHistoryResponseOutcome.Failed((CaseGetResponseOutcomeFailedJson.read) value)
        | "CANCELLED" ->
            CaseHistoryResponseOutcome.Cancelled((CaseGetResponseOutcomeCancelledJson.read) value)
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: CaseHistoryResponseOutcome) =
        match value with
        | CaseHistoryResponseOutcome.Succeeded item ->
            (CaseHistoryResponseOutcomeSucceededJson.write) writer item
        | CaseHistoryResponseOutcome.Rejected item ->
            (CaseGetResponseOutcomeRejectedJson.write) writer item
        | CaseHistoryResponseOutcome.Failed item ->
            (CaseGetResponseOutcomeFailedJson.write) writer item
        | CaseHistoryResponseOutcome.Cancelled item ->
            (CaseGetResponseOutcomeCancelledJson.write) writer item

module internal CaseHistoryResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : CaseHistoryResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar61.read) value
            Outcome = JsonRead.required "outcome" (CaseHistoryResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseHistoryResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar61.write) writer value.Endpoint
        JsonWrite.property "outcome" (CaseHistoryResponseOutcomeJson.write) writer value.Outcome
        writer.WriteEndObject()

module internal CaseHistoryRequestJson =
    let private properties = [ "caseReference"; "cursor"; "limit"; "detail" ]

    let read (value: JsonElement) : CaseHistoryRequest =
        JsonRead.objectValue properties value

        {
            CaseReference = JsonRead.required "caseReference" (ProtocolScalar5.read) value
            Cursor = JsonRead.optional "cursor" (ProtocolScalar8.read) value
            Limit = JsonRead.required "limit" (ProtocolScalar60.read) value
            Detail = JsonRead.required "detail" (ProtocolScalar62.read) value
        }

    let write (writer: Utf8JsonWriter) (value: CaseHistoryRequest) =
        writer.WriteStartObject()
        JsonWrite.property "caseReference" (ProtocolScalar5.write) writer value.CaseReference
        JsonWrite.optional "cursor" (ProtocolScalar8.write) writer value.Cursor
        JsonWrite.property "limit" (ProtocolScalar60.write) writer value.Limit
        JsonWrite.property "detail" (ProtocolScalar62.write) writer value.Detail
        writer.WriteEndObject()

module internal OperationObserveResponseOutcomeSucceededDataFoundJson =
    let private properties = [ "tag"; "receipt" ]

    let read (value: JsonElement) : OperationObserveResponseOutcomeSucceededDataFound =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar44.read) value
            Receipt = JsonRead.required "receipt" (ReceiptJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: OperationObserveResponseOutcomeSucceededDataFound) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar44.write) writer value.Tag
        JsonWrite.property "receipt" (ReceiptJson.write) writer value.Receipt
        writer.WriteEndObject()

module internal OperationObserveResponseOutcomeSucceededDataNotFoundJson =
    let private properties = [ "tag"; "operationId" ]

    let read (value: JsonElement) : OperationObserveResponseOutcomeSucceededDataNotFound =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar45.read) value
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
        }

    let write
        (writer: Utf8JsonWriter)
        (value: OperationObserveResponseOutcomeSucceededDataNotFound)
        =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar45.write) writer value.Tag
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        writer.WriteEndObject()

module internal OperationObserveResponseOutcomeSucceededDataJson =
    let read (value: JsonElement) : OperationObserveResponseOutcomeSucceededData =
        match JsonRead.tag "tag" value with
        | "FOUND" ->
            OperationObserveResponseOutcomeSucceededData.Found(
                (OperationObserveResponseOutcomeSucceededDataFoundJson.read) value
            )
        | "NOT_FOUND" ->
            OperationObserveResponseOutcomeSucceededData.NotFound(
                (OperationObserveResponseOutcomeSucceededDataNotFoundJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: OperationObserveResponseOutcomeSucceededData) =
        match value with
        | OperationObserveResponseOutcomeSucceededData.Found item ->
            (OperationObserveResponseOutcomeSucceededDataFoundJson.write) writer item
        | OperationObserveResponseOutcomeSucceededData.NotFound item ->
            (OperationObserveResponseOutcomeSucceededDataNotFoundJson.write) writer item

module internal OperationObserveResponseOutcomeSucceededJson =
    let private properties = [ "tag"; "data" ]

    let read (value: JsonElement) : OperationObserveResponseOutcomeSucceeded =
        JsonRead.objectValue properties value

        {
            Tag = JsonRead.required "tag" (ProtocolScalar55.read) value
            Data =
                JsonRead.required
                    "data"
                    (OperationObserveResponseOutcomeSucceededDataJson.read)
                    value
        }

    let write (writer: Utf8JsonWriter) (value: OperationObserveResponseOutcomeSucceeded) =
        writer.WriteStartObject()
        JsonWrite.property "tag" (ProtocolScalar55.write) writer value.Tag

        JsonWrite.property
            "data"
            (OperationObserveResponseOutcomeSucceededDataJson.write)
            writer
            value.Data

        writer.WriteEndObject()

module internal OperationObserveResponseOutcomeJson =
    let read (value: JsonElement) : OperationObserveResponseOutcome =
        match JsonRead.tag "tag" value with
        | "SUCCEEDED" ->
            OperationObserveResponseOutcome.Succeeded(
                (OperationObserveResponseOutcomeSucceededJson.read) value
            )
        | "REJECTED" ->
            OperationObserveResponseOutcome.Rejected(
                (CaseGetResponseOutcomeRejectedJson.read) value
            )
        | "FAILED" ->
            OperationObserveResponseOutcome.Failed((CaseGetResponseOutcomeFailedJson.read) value)
        | "CANCELLED" ->
            OperationObserveResponseOutcome.Cancelled(
                (CaseGetResponseOutcomeCancelledJson.read) value
            )
        | _ -> JsonInput.reject "INVALID_VALUE"

    let write (writer: Utf8JsonWriter) (value: OperationObserveResponseOutcome) =
        match value with
        | OperationObserveResponseOutcome.Succeeded item ->
            (OperationObserveResponseOutcomeSucceededJson.write) writer item
        | OperationObserveResponseOutcome.Rejected item ->
            (CaseGetResponseOutcomeRejectedJson.write) writer item
        | OperationObserveResponseOutcome.Failed item ->
            (CaseGetResponseOutcomeFailedJson.write) writer item
        | OperationObserveResponseOutcome.Cancelled item ->
            (CaseGetResponseOutcomeCancelledJson.write) writer item

module internal OperationObserveResponseJson =
    let private properties = [ "endpoint"; "outcome" ]

    let read (value: JsonElement) : OperationObserveResponse =
        JsonRead.objectValue properties value

        {
            Endpoint = JsonRead.required "endpoint" (ProtocolScalar63.read) value
            Outcome = JsonRead.required "outcome" (OperationObserveResponseOutcomeJson.read) value
        }

    let write (writer: Utf8JsonWriter) (value: OperationObserveResponse) =
        writer.WriteStartObject()
        JsonWrite.property "endpoint" (ProtocolScalar63.write) writer value.Endpoint

        JsonWrite.property
            "outcome"
            (OperationObserveResponseOutcomeJson.write)
            writer
            value.Outcome

        writer.WriteEndObject()

module internal OperationObserveRequestJson =
    let private properties = [ "operationId" ]

    let read (value: JsonElement) : OperationObserveRequest =
        JsonRead.objectValue properties value

        {
            OperationId = JsonRead.required "operationId" (ProtocolScalar10.read) value
        }

    let write (writer: Utf8JsonWriter) (value: OperationObserveRequest) =
        writer.WriteStartObject()
        JsonWrite.property "operationId" (ProtocolScalar10.write) writer value.OperationId
        writer.WriteEndObject()
