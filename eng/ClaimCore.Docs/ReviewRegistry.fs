namespace ClaimCore.Docs

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module ReviewRegistry =
    let path = "eng/ClaimCore.Docs/contract-reviews.json"
    let private digest = Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)

    let private exact (expected: string list) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error "Expected a JSON object."
        else
            let actual = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList

            if actual.Length <> (actual |> Set.ofList |> Set.count) then
                Error "Duplicate properties are forbidden."
            elif Set.ofList actual <> Set.ofList expected then
                Error "Object has missing or unknown properties."
            else
                Ok()

    let private text (name: string) (element: JsonElement) =
        let value = element.GetProperty(name)

        match value.ValueKind, value.GetString() |> Option.ofObj with
        | JsonValueKind.String, Some content -> Ok content
        | _ -> Error $"'{name}' must be a non-null string."

    let private textList (name: string) (element: JsonElement) =
        let values = element.GetProperty(name)

        if values.ValueKind <> JsonValueKind.Array then
            Error $"'{name}' must be an array."
        else
            let parsed = values.EnumerateArray() |> Seq.toList

            if
                parsed
                |> List.exists (fun value ->
                    value.ValueKind <> JsonValueKind.String
                    || (value.GetString() |> Option.ofObj).IsNone)
            then
                Error $"'{name}' entries must be non-null strings."
            else
                Ok(parsed |> List.choose (fun value -> value.GetString() |> Option.ofObj))

    let private parseSpan (element: JsonElement) =
        exact [ "path"; "beginMarker"; "endMarker" ] element
        |> Result.bind (fun () ->
            match text "path" element, text "beginMarker" element, text "endMarker" element with
            | Ok path, Ok beginMarker, Ok endMarker ->
                if
                    beginMarker.Length < 8
                    || endMarker.Length < 8
                    || beginMarker.Contains('\n')
                    || endMarker.Contains('\n')
                    || beginMarker = endMarker
                then
                    Error "Review span markers must be distinct, substantive single-line strings."
                else
                    Ok
                        {
                            Path = path
                            BeginMarker = beginMarker
                            EndMarker = endMarker
                        }
            | _ -> Error "Review span has invalid properties.")

    let private parsedSpans (element: JsonElement) =
        let spans = element.GetProperty("markedSpans")

        if spans.ValueKind <> JsonValueKind.Array then
            Error "'markedSpans' must be an array."
        else
            let parsed = spans.EnumerateArray() |> Seq.map parseSpan |> Seq.toList

            match
                parsed
                |> List.tryPick (function
                    | Error value -> Some value
                    | _ -> None)
            with
            | Some error -> Error error
            | None ->
                Ok(
                    parsed
                    |> List.choose (function
                        | Ok value -> Some value
                        | _ -> None)
                )

    let private buildReview
        (contract: string)
        (reviewerKind: string)
        (reviewer: string)
        (rawDate: string)
        (conclusion: string)
        (subject: string)
        (files: string list)
        (spans: ReviewSpan list)
        =
        let mutable reviewedOn = DateOnly.MinValue

        let dateOk =
            DateOnly.TryParseExact(
                rawDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                &reviewedOn
            )

        if not dateOk then
            Error "'reviewedOn' must be an ISO calendar date."
        elif reviewerKind <> "human" && reviewerKind <> "agent" then
            Error "'reviewerKind' must be 'human' or 'agent'."
        elif reviewer.Trim().Length < 3 then
            Error "A review requires a substantive reviewer identity."
        elif conclusion <> "approved" then
            Error "A required semantic review conclusion must be 'approved'."
        elif not (digest.IsMatch(subject)) then
            Error "'reviewSubjectHash' must be lowercase SHA-256."
        else
            Ok
                {
                    ContractId = contract
                    ReviewerKind = reviewerKind
                    Reviewer = reviewer.Trim()
                    ReviewedOn = reviewedOn
                    Conclusion = conclusion
                    ReviewSubjectHash = subject
                    WholeFiles = files
                    MarkedSpans = spans
                }

    let private parseReview (element: JsonElement) =
        let properties =
            [
                "contractId"
                "reviewerKind"
                "reviewer"
                "reviewedOn"
                "conclusion"
                "reviewSubjectHash"
                "wholeFiles"
                "markedSpans"
            ]

        match exact properties element with
        | Error message -> Error message
        | Ok() ->
            match
                text "contractId" element,
                text "reviewerKind" element,
                text "reviewer" element,
                text "reviewedOn" element,
                text "conclusion" element,
                text "reviewSubjectHash" element,
                textList "wholeFiles" element,
                parsedSpans element
            with
            | Ok contract,
              Ok reviewerKind,
              Ok reviewer,
              Ok date,
              Ok conclusion,
              Ok subject,
              Ok files,
              Ok spans ->
                buildReview contract reviewerKind reviewer date conclusion subject files spans
            | _ -> Error "Review has invalid properties."

    let load (root: RepositoryRoot) =
        try
            match Repository.registeredPath root path with
            | Error message -> Error message
            | Ok candidate ->
                match Repository.ensureExistingSafe root candidate with
                | Error message -> Error message
                | Ok safe ->
                    use document = JsonDocument.Parse(File.ReadAllBytes(safe))
                    let element = document.RootElement

                    exact [ "schemaVersion"; "reviews" ] element
                    |> Result.bind (fun () ->
                        let mutable version = 0
                        let reviews = element.GetProperty("reviews")

                        if
                            not (element.GetProperty("schemaVersion").TryGetInt32(&version))
                            || version <> 1
                        then
                            Error "Unsupported contract-review schema version."
                        elif reviews.ValueKind <> JsonValueKind.Array then
                            Error "'reviews' must be an array."
                        else
                            let parsed =
                                reviews.EnumerateArray() |> Seq.map parseReview |> Seq.toList

                            match
                                parsed
                                |> List.tryPick (function
                                    | Error value -> Some value
                                    | _ -> None)
                            with
                            | Some error -> Error error
                            | None ->
                                Ok(
                                    parsed
                                    |> List.choose (function
                                        | Ok value -> Some value
                                        | _ -> None)
                                ))
        with error ->
            Error(error.GetType().Name + ": " + error.Message)
