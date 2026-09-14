// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal ProtocolScalar0 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue
            (Some "date")
            (Some "\\A(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}\\z")
            (Some 10)
            (Some 10))
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar1 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue
            None
            (Some
                "\\A(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]*\\z)(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000])(?![\\s\\S]*[\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]\\z)(?![\\s\\S]*[\\u0000-\\u001f\\u007f-\\u009f])[\\s\\S]*\\z")
            (Some 1)
            (Some 100))
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar2 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue
            None
            (Some
                "\\A(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]*\\z)(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000])(?![\\s\\S]*[\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]\\z)(?![\\s\\S]*[\\u0000-\\u001f\\u007f-\\u009f])[\\s\\S]*\\z")
            (Some 1)
            (Some 200))
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar3 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue
            None
            (Some
                "\\A(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]*\\z)(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000])(?![\\s\\S]*[\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]\\z)(?![\\s\\S]*[\\u0000-\\u001f\\u007f-\\u009f])(?:(0|[1-9][0-9]{0,17})(\\.[0-9]{1,4})?)\\z")
            (Some 1)
            (Some 23))
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar4 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue
            None
            (Some
                "\\A(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]*\\z)(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000])(?![\\s\\S]*[\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]\\z)(?![\\s\\S]*[\\u0000-\\u001f\\u007f-\\u009f])(?:[A-Z]{3})\\z")
            (Some 1)
            (Some 3))
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar5 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue
            None
            (Some
                "\\A(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]*\\z)(?![\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000])(?![\\s\\S]*[\\u0009-\\u000d\\u0020\\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000]\\z)(?![\\s\\S]*[\\u0000-\\u001f\\u007f-\\u009f])[\\s\\S]*\\z")
            (Some 1)
            (Some 80))
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar6 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "OPENED"; "CLOSED" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar7 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue
            None
            (Some
                "\\A(?:0|[1-9][0-9]{0,17}|[1-8][0-9]{18}|9[0-1][0-9]{17}|92[0-1][0-9]{16}|922[0-2][0-9]{15}|9223[0-2][0-9]{14}|92233[0-6][0-9]{13}|922337[0-1][0-9]{12}|92233720[0-2][0-9]{10}|922337203[0-5][0-9]{9}|9223372036[0-7][0-9]{8}|92233720368[0-4][0-9]{7}|922337203685[0-3][0-9]{6}|9223372036854[0-6][0-9]{5}|92233720368547[0-6][0-9]{4}|922337203685477[0-4][0-9]{3}|9223372036854775[0-7][0-9]{2}|922337203685477580[0-5]|9223372036854775806)\\z")
            (Some 1)
            (Some 19))
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar8 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue None None (Some 1) None) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar9 =
    let read (value: JsonElement) : bool = (ScalarRead.boolean) value
    let write (writer: Utf8JsonWriter) (value: bool) = (JsonWrite.boolean) writer value

module internal ProtocolScalar10 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue
            (Some "uuid")
            (Some
                "\\A(?!00000000-0000-0000-0000-000000000000\\z)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\z")
            (Some 36)
            (Some 36))
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar11 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration
            [
                "OPEN"
                "AMEND_REGISTRATION"
                "DECIDE"
                "WITHDRAW_DECISION"
                "RECORD_PAYMENT"
                "CLEAR_PAYMENT"
                "CLOSE"
                "REOPEN"
            ])
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar12 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue
            (Some "date-time")
            (Some
                "\\A(?!0000-)[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}\\\u002B00:00\\z")
            (Some 33)
            (Some 33))
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar13 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "BLANK")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "BLANK" JsonWrite.text) writer value

module internal ProtocolScalar14 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "CURRENT_FIELD")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "CURRENT_FIELD" JsonWrite.text) writer value

module internal ProtocolScalar15 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "ACCEPTED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "ACCEPTED" JsonWrite.text) writer value

module internal ProtocolScalar16 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "REJECTED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "REJECTED" JsonWrite.text) writer value

module internal ProtocolScalar17 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration
            [
                "INVALID_INPUT"
                "CASE_NOT_FOUND"
                "CASE_ALREADY_EXISTS"
                "VERSION_CONFLICT"
                "CASE_CLOSED"
                "AMENDMENT_REQUIRES_UNDECIDED"
                "DECISION_REQUIRED"
                "PAYMENT_ALREADY_RECORDED"
                "PAYMENT_NOT_RECORDED"
                "DECISION_ALREADY_PAID"
                "ALREADY_CLOSED"
                "ALREADY_OPENED"
                "ZERO_DECISION_CANNOT_BE_PAID"
                "IDEMPOTENCY_CONFLICT"
            ])
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar18 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration
            [
                "CORRECT_INPUT"
                "READ_CURRENT"
                "RETRY_SAFE"
                "RECOVER_EXACT"
                "REAUTHENTICATE"
                "STOP_AND_INVESTIGATE"
                "NONE_REQUIRED"
            ])
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar19 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "FAILED_BEFORE_COMMIT")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "FAILED_BEFORE_COMMIT" JsonWrite.text) writer value

module internal ProtocolScalar20 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration
            [
                "STORE_UNAVAILABLE"
                "STORE_INTEGRITY_ERROR"
                "SCHEMA_MISMATCH"
                "RECOVERY_CAPACITY_EXCEEDED"
                "RECOVERY_INTEGRITY_ERROR"
                "COMMIT_OUTCOME_UNKNOWN"
                "TECHNICAL_MUTATION_UNKNOWN"
            ])
            value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar21 =
    let read (value: JsonElement) : string =
        (ScalarRead.stringValue None (Some "\\A[0-9a-f]{64}\\z") (Some 64) (Some 64)) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar22 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "SEMANTIC_CORE_V1")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "SEMANTIC_CORE_V1" JsonWrite.text) writer value

module internal ProtocolScalar23 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "ClaimCore")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "ClaimCore" JsonWrite.text) writer value

module internal ProtocolScalar24 =
    let read (value: JsonElement) : string =
        ((fun item ->
            ScalarRead.text item
            |> ScalarRead.literal "trusted-local-operator-claims-register"))
            value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "trusted-local-operator-claims-register" JsonWrite.text) writer value

module internal ProtocolScalar25 =
    let read (value: JsonElement) : int64 =
        ((fun item -> ScalarRead.integer None None item |> ScalarRead.literal 2L)) value

    let write (writer: Utf8JsonWriter) (value: int64) =
        (JsonWrite.literal 2L JsonWrite.integer) writer value

module internal ProtocolScalar26 =
    let read (value: JsonElement) : int64 =
        ((fun item -> ScalarRead.integer None None item |> ScalarRead.literal 1L)) value

    let write (writer: Utf8JsonWriter) (value: int64) =
        (JsonWrite.literal 1L JsonWrite.integer) writer value
