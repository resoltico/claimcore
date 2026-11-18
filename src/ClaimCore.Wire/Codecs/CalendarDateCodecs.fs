namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Json

module internal CalendarDateCodecs =
    open Codec

    let private characters = CalendarDate.ExactFormat.Length
    let private pattern = "^" + CalendarDate.Grammar + @"(?![\s\S])"

    let text =
        patternedText characters characters pattern (Some "date")
        |> restrict
            (Schema.String(Some pattern, Some "date", Some characters, Some characters, None))
            (fun path value ->
                Codec.text.Read path value
                |> Result.bind (fun raw ->
                    if CalendarDate.parse raw |> Result.isOk then
                        Ok()
                    else
                        Error
                            {
                                Path = path
                                Reason = "Expected a Gregorian date."
                            }))

    let value =
        text
        |> map (CalendarDate.parse >> Result.toOption >> Option.get) CalendarDate.format
