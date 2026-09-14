// Generated from ClaimCore.Contracts. Do not edit.
namespace ClaimCore.Protocol

open System.Text.Json

module internal ProtocolScalar66 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "OBSERVED_ACCEPTED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "OBSERVED_ACCEPTED" JsonWrite.text) writer value

module internal ProtocolScalar67 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "RETAINED_FOR_RECOVERY")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "RETAINED_FOR_RECOVERY" JsonWrite.text) writer value

module internal ProtocolScalar68 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "CANCELLED_BEFORE_ADMISSION"))
            value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "CANCELLED_BEFORE_ADMISSION" JsonWrite.text) writer value

module internal ProtocolScalar69 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "PREPARATION_STATE_UNKNOWN")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "PREPARATION_STATE_UNKNOWN" JsonWrite.text) writer value

module internal ProtocolScalar70 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "OPEN")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "OPEN" JsonWrite.text) writer value

module internal ProtocolScalar71 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "AMEND_REGISTRATION")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "AMEND_REGISTRATION" JsonWrite.text) writer value

module internal ProtocolScalar72 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "DECIDE")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "DECIDE" JsonWrite.text) writer value

module internal ProtocolScalar73 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "WITHDRAW_DECISION")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "WITHDRAW_DECISION" JsonWrite.text) writer value

module internal ProtocolScalar74 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "RECORD_PAYMENT")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "RECORD_PAYMENT" JsonWrite.text) writer value

module internal ProtocolScalar75 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "CLEAR_PAYMENT")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "CLEAR_PAYMENT" JsonWrite.text) writer value

module internal ProtocolScalar76 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "CLOSE")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "CLOSE" JsonWrite.text) writer value

module internal ProtocolScalar77 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "REOPEN")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "REOPEN" JsonWrite.text) writer value

module internal ProtocolScalar78 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "command.execute")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "command.execute" JsonWrite.text) writer value

module internal ProtocolScalar79 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "COMPLETED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "COMPLETED" JsonWrite.text) writer value

module internal ProtocolScalar80 =
    let read (value: JsonElement) : string =
        (ScalarRead.enumeration [ "CONFIRMED"; "UNCONFIRMED" ]) value

    let write (writer: Utf8JsonWriter) (value: string) = (JsonWrite.text) writer value

module internal ProtocolScalar81 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "REFUSED_BEFORE_ATTEMPT")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "REFUSED_BEFORE_ATTEMPT" JsonWrite.text) writer value

module internal ProtocolScalar82 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "FAILED_BEFORE_ATTEMPT")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "FAILED_BEFORE_ATTEMPT" JsonWrite.text) writer value

module internal ProtocolScalar83 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "CANCELLED_BEFORE_ATTEMPT")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "CANCELLED_BEFORE_ATTEMPT" JsonWrite.text) writer value

module internal ProtocolScalar84 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "ATTEMPT_ADMISSION_UNKNOWN")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "ATTEMPT_ADMISSION_UNKNOWN" JsonWrite.text) writer value

module internal ProtocolScalar85 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "ATTEMPT_UNRESOLVED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "ATTEMPT_UNRESOLVED" JsonWrite.text) writer value

module internal ProtocolScalar86 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "recovery.list")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "recovery.list" JsonWrite.text) writer value

module internal ProtocolScalar87 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "recovery.inspect")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "recovery.inspect" JsonWrite.text) writer value

module internal ProtocolScalar88 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "recovery.resolve")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "recovery.resolve" JsonWrite.text) writer value

module internal ProtocolScalar89 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "recovery.dismiss")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "recovery.dismiss" JsonWrite.text) writer value

module internal ProtocolScalar90 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "DISMISSED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "DISMISSED" JsonWrite.text) writer value

module internal ProtocolScalar91 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "ALREADY_DISMISSED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "ALREADY_DISMISSED" JsonWrite.text) writer value

module internal ProtocolScalar92 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "REFUSED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "REFUSED" JsonWrite.text) writer value

module internal ProtocolScalar93 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "DISMISS_STATE_UNKNOWN")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "DISMISS_STATE_UNKNOWN" JsonWrite.text) writer value

module internal ProtocolScalar94 =
    let read (value: JsonElement) : bool =
        ((fun item -> ScalarRead.boolean item |> ScalarRead.literal true)) value

    let write (writer: Utf8JsonWriter) (value: bool) =
        (JsonWrite.literal true JsonWrite.boolean) writer value

module internal ProtocolScalar95 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "recovery.export")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "recovery.export" JsonWrite.text) writer value

module internal ProtocolScalar96 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "recovery.importEnvelopePreview"))
            value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "recovery.importEnvelopePreview" JsonWrite.text) writer value

module internal ProtocolScalar97 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "recovery.importEnvelopeRetain"))
            value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "recovery.importEnvelopeRetain" JsonWrite.text) writer value

module internal ProtocolScalar98 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "RETAINED")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "RETAINED" JsonWrite.text) writer value

module internal ProtocolScalar99 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "EXISTING")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "EXISTING" JsonWrite.text) writer value

module internal ProtocolScalar100 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "RETAIN_STATE_UNKNOWN")) value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "RETAIN_STATE_UNKNOWN" JsonWrite.text) writer value

module internal ProtocolScalar101 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "recovery.importRecordPreview"))
            value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "recovery.importRecordPreview" JsonWrite.text) writer value

module internal ProtocolScalar102 =
    let read (value: JsonElement) : string =
        ((fun item -> ScalarRead.text item |> ScalarRead.literal "recovery.importRecordRetain"))
            value

    let write (writer: Utf8JsonWriter) (value: string) =
        (JsonWrite.literal "recovery.importRecordRetain" JsonWrite.text) writer value
