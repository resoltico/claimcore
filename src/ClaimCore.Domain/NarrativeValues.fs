namespace ClaimCore.Domain

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type IncidentDescription = private IncidentDescription of string

module IncidentDescription =
    [<Literal>]
    let MaximumScalars = 600

    [<Literal>]
    let MaximumBytes = 4096

    let constraints: PlainTextConstraints =
        {
            MaximumScalars = MaximumScalars
            MaximumUtf8Bytes = MaximumBytes
            AllowsLineFeed = true
        }

    let format (IncidentDescription value) = value

    /// Retained representability does not apply today's admission policy to retained text.
    let parse value =
        ScalarText.parse (Some constraints.MaximumUtf8Bytes) constraints.MaximumScalars value
        |> Result.map IncidentDescription

    let admit (IncidentDescription value) =
        ScalarText.admitPlain constraints.MaximumScalars constraints.AllowsLineFeed value
        |> Result.map IncidentDescription

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type HandlingSummary = private HandlingSummary of string

module HandlingSummary =
    [<Literal>]
    let MaximumScalars = 600

    [<Literal>]
    let MaximumBytes = 4096

    let constraints: PlainTextConstraints =
        {
            MaximumScalars = MaximumScalars
            MaximumUtf8Bytes = MaximumBytes
            AllowsLineFeed = true
        }

    let format (HandlingSummary value) = value

    let parse value =
        ScalarText.parse (Some constraints.MaximumUtf8Bytes) constraints.MaximumScalars value
        |> Result.map HandlingSummary

    let admit (HandlingSummary value) =
        ScalarText.admitPlain constraints.MaximumScalars constraints.AllowsLineFeed value
        |> Result.map HandlingSummary

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type ReminderAction = private ReminderAction of string

module ReminderAction =
    [<Literal>]
    let MaximumScalars = 200

    [<Literal>]
    let MaximumBytes = 1024

    let constraints: PlainTextConstraints =
        {
            MaximumScalars = MaximumScalars
            MaximumUtf8Bytes = MaximumBytes
            AllowsLineFeed = false
        }

    let format (ReminderAction value) = value

    let parse value =
        ScalarText.parse (Some constraints.MaximumUtf8Bytes) constraints.MaximumScalars value
        |> Result.map ReminderAction

    let admit (ReminderAction value) =
        ScalarText.admitPlain constraints.MaximumScalars constraints.AllowsLineFeed value
        |> Result.map ReminderAction
