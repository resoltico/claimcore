// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal ProtocolScalar27 =
    let read (value: JsonElement) : int64 =
        ((fun item -> ScalarRead.integer None None item |> ScalarRead.literal 50L)) value

    let write (writer: Utf8JsonWriter) (value: int64) =
        (JsonWrite.literal 50L JsonWrite.integer) writer value

module internal ProtocolScalar28 =
    let read (value: JsonElement) : int64 =
        ((fun item -> ScalarRead.integer None None item |> ScalarRead.literal 65536L)) value

    let write (writer: Utf8JsonWriter) (value: int64) =
        (JsonWrite.literal 65536L JsonWrite.integer) writer value

module internal ProtocolScalar29 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "TEXT")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "TEXT" JsonWrite.text) writer value

module internal ProtocolScalar30 =
    let read (value: JsonElement) : int64 =
        (ScalarRead.integer (Some 0L) None) value

    let write (writer: Utf8JsonWriter) (value: int64) = (JsonWrite.integer) writer value

module internal ProtocolScalar31 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "CALENDAR_DATE")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "CALENDAR_DATE" JsonWrite.text) writer value

module internal ProtocolScalar32 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "AMOUNT")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "AMOUNT" JsonWrite.text) writer value

module internal ProtocolScalar33 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "CURRENCY")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "CURRENCY" JsonWrite.text) writer value

module internal ProtocolScalar34 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "CASE_STATUS")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "CASE_STATUS" JsonWrite.text) writer value

module internal ProtocolScalar35 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "CROSS_FIELD"; "TRANSITION" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar36 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "SUMMARY")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "SUMMARY" JsonWrite.text) writer value

module internal ProtocolScalar37 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "FULL")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "FULL" JsonWrite.text) writer value

module internal ProtocolScalar38 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "HOST_FAILURE")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "HOST_FAILURE" JsonWrite.text) writer value

module internal ProtocolScalar39 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "NOT_STARTED"; "STARTED_UNCONFIRMED" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar40 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "UNSUBMITTED"; "SUBMISSION_STARTED"; "DISMISSED" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar41 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "RESOLVE"; "DISMISS"; "EXPORT" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar42 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "LEGACY_UNCLASSIFIED"; "SEMANTIC_CORE_V1" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar43 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "ACCEPTED"; "REJECTED"; "ERROR" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar44 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "FOUND")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "FOUND" JsonWrite.text) writer value

module internal ProtocolScalar45 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "NOT_FOUND")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "NOT_FOUND" JsonWrite.text) writer value

module internal ProtocolScalar46 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "ENVELOPE"; "UNBOUND_CANONICAL_RECORD" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar47 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration
            [
                "INVALID_RECOVERY_INPUT"
                "PREPARATION_NOT_FOUND"
                "RECOVERY_IDEMPOTENCY_CONFLICT"
                "PREPARATION_DISMISSED"
                "SUBMISSION_ALREADY_STARTED"
                "RECOVERY_ACTION_UNAVAILABLE"
                "SOURCE_DIGEST_MISMATCH"
                "INSTALLATION_MISMATCH"
                "UNSUPPORTED_RECOVERY_ARTIFACT"
            ])
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar48 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "session")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "session" JsonWrite.text) writer value

module internal ProtocolScalar49 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "SNAPSHOT")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "SNAPSHOT" JsonWrite.text) writer value

module internal ProtocolScalar50 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "session.login")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "session.login" JsonWrite.text) writer value

module internal ProtocolScalar51 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "session.logout")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "session.logout" JsonWrite.text) writer value

module internal ProtocolScalar52 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "definition")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "definition" JsonWrite.text) writer value

module internal ProtocolScalar53 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "DESCRIBED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "DESCRIBED" JsonWrite.text) writer value

module internal ProtocolScalar54 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "case.get")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "case.get" JsonWrite.text) writer value

module internal ProtocolScalar55 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "SUCCEEDED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "SUCCEEDED" JsonWrite.text) writer value

module internal ProtocolScalar56 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "FAILED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "FAILED" JsonWrite.text) writer value

module internal ProtocolScalar57 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "CANCELLED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "CANCELLED" JsonWrite.text) writer value

module internal ProtocolScalar58 =
    let read (value: JsonElement) : unit = (ScalarRead.nullValue) value
    let write (writer: Utf8JsonWriter) (value: unit) = (JsonWrite.nullValue) writer value

module internal ProtocolScalar59 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "case.list")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "case.list" JsonWrite.text) writer value

module internal ProtocolScalar60 =
    let read (value: JsonElement) : int64 =
        (ScalarRead.integer (Some 1L) (Some 50L)) value

    let write (writer: Utf8JsonWriter) (value: int64) = (JsonWrite.integer) writer value

module internal ProtocolScalar61 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "case.history")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "case.history" JsonWrite.text) writer value

module internal ProtocolScalar62 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "SUMMARY"; "FULL" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar63 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "operation.observe")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "operation.observe" JsonWrite.text) writer value

module internal ProtocolScalar64 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "command.prepare")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "command.prepare" JsonWrite.text) writer value

module internal ProtocolScalar65 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "PREPARED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "PREPARED" JsonWrite.text) writer value
