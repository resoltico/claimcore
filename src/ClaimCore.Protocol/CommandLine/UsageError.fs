namespace ClaimCore.Protocol


open System
open ClaimCore.Json

[<RequireQualifiedAccess>]
type UsageError =
    | UnknownCommand of command: string * suggestions: string list
    | UnknownOption of flag: string * suggestion: string option
    | MissingOption of flag: string
    | MissingOperand of operand: string
    | ExtraOperand of operand: string
    | InvalidValue of flag: string * value: string
    | InvalidChoice of flag: string * value: string * allowed: string list
    | NotApplicable of flag: string * because: string
    | DuplicateOption of flag: string
    | MissingValue of flag: string
    | RequestTooLarge of maximumBytes: int
    | UnknownTopic of topic: string

module UsageError =
    open Codec

    let private selections =
        [
            variant
                "UNKNOWN_COMMAND"
                UsageError.UnknownCommand
                (function
                | UsageError.UnknownCommand(command, suggestions) -> Some(command, suggestions)
                | _ -> None)
                (record (fun command suggestions -> command, suggestions)
                 |> field "command" fst text
                 |> field "suggestions" snd (list Int32.MaxValue text))
            variant
                "UNKNOWN_OPTION"
                UsageError.UnknownOption
                (function
                | UsageError.UnknownOption(flag, suggestion) -> Some(flag, suggestion)
                | _ -> None)
                (record (fun flag suggestion -> flag, suggestion)
                 |> field "flag" fst text
                 |> field "suggestion" snd (nullable text))
            variant
                "MISSING_OPTION"
                UsageError.MissingOption
                (function
                | UsageError.MissingOption flag -> Some flag
                | _ -> None)
                (record id |> field "flag" id text)
            variant
                "MISSING_OPERAND"
                UsageError.MissingOperand
                (function
                | UsageError.MissingOperand operand -> Some operand
                | _ -> None)
                (record id |> field "operand" id text)
        ]

    let private values =
        [
            variant
                "EXTRA_OPERAND"
                UsageError.ExtraOperand
                (function
                | UsageError.ExtraOperand operand -> Some operand
                | _ -> None)
                (record id |> field "operand" id text)
            variant
                "INVALID_VALUE"
                UsageError.InvalidValue
                (function
                | UsageError.InvalidValue(flag, value) -> Some(flag, value)
                | _ -> None)
                (record (fun flag value -> flag, value)
                 |> field "flag" fst text
                 |> field "value" snd text)
            variant
                "INVALID_CHOICE"
                UsageError.InvalidChoice
                (function
                | UsageError.InvalidChoice(flag, value, allowed) -> Some(flag, value, allowed)
                | _ -> None)
                (record (fun flag value allowed -> flag, value, allowed)
                 |> field "flag" (fun (flag, _, _) -> flag) text
                 |> field "value" (fun (_, value, _) -> value) text
                 |> field "allowed" (fun (_, _, allowed) -> allowed) (list Int32.MaxValue text))
            variant
                "NOT_APPLICABLE"
                UsageError.NotApplicable
                (function
                | UsageError.NotApplicable(flag, because) -> Some(flag, because)
                | _ -> None)
                (record (fun flag because -> flag, because)
                 |> field "flag" fst text
                 |> field "because" snd text)
        ]

    let private cardinality =
        [
            variant
                "DUPLICATE_OPTION"
                UsageError.DuplicateOption
                (function
                | UsageError.DuplicateOption flag -> Some flag
                | _ -> None)
                (record id |> field "flag" id text)
            variant
                "MISSING_VALUE"
                UsageError.MissingValue
                (function
                | UsageError.MissingValue flag -> Some flag
                | _ -> None)
                (record id |> field "flag" id text)
            variant
                "REQUEST_TOO_LARGE"
                UsageError.RequestTooLarge
                (function
                | UsageError.RequestTooLarge maximum -> Some maximum
                | _ -> None)
                (record id
                 |> field "maximumBytes" id (integer 0L (int64 Int32.MaxValue) |> map int int64))
            variant
                "UNKNOWN_TOPIC"
                UsageError.UnknownTopic
                (function
                | UsageError.UnknownTopic topic -> Some topic
                | _ -> None)
                (record id |> field "topic" id text)
        ]

    let codec = tagged "UsageError" (selections @ values @ cardinality)
