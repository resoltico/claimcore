namespace ClaimCore.Docs

open System
open System.Collections.Generic
open System.IO
open System.Text

[<RequireQualifiedAccess>]
module Reviews =
    let private utf8 = UTF8Encoding(false, true)

    let private exactlyOnce (marker: string) (text: string) =
        let first = text.IndexOf(marker, StringComparison.Ordinal)

        if
            first < 0
            || text.IndexOf(marker, first + marker.Length, StringComparison.Ordinal) >= 0
        then
            Error $"Marker '{marker}' must occur exactly once."
        else
            Ok first

    let private subjectRecord (kind: string) (path: string) (bytes: byte array) =
        kind
        + string (char 0)
        + path
        + string (char 0)
        + Repository.sha256Bytes bytes
        + "\n"

    let subjectHash
        (root: RepositoryRoot)
        (declaration: ContractDeclaration)
        (review: ContractReview)
        =
        let errors = ResizeArray<string>()
        let records = ResizeArray<string>()
        records.Add(subjectRecord "contract-section" declaration.Document declaration.SectionBytes)

        let addWhole (relative: string) =
            if relative = ReviewRegistry.path then
                errors.Add("The review registry cannot review itself.")
            else
                match Repository.registeredPath root relative with
                | Error message -> errors.Add(message)
                | Ok path ->
                    match Repository.ensureExistingSafe root path with
                    | Error message -> errors.Add(message)
                    | Ok safe ->
                        records.Add(subjectRecord "file" relative (File.ReadAllBytes(safe)))

        let addSpan (span: ReviewSpan) =
            if span.Path = ReviewRegistry.path then
                errors.Add("The review registry cannot review itself.")
            else
                match Repository.registeredPath root span.Path with
                | Error message -> errors.Add(message)
                | Ok path ->
                    match Repository.ensureExistingSafe root path with
                    | Error message -> errors.Add(message)
                    | Ok safe ->
                        try
                            let text = utf8.GetString(File.ReadAllBytes(safe))

                            match
                                exactlyOnce span.BeginMarker text, exactlyOnce span.EndMarker text
                            with
                            | Ok first, Ok last when first + span.BeginMarker.Length < last ->
                                let content =
                                    text.Substring(first, last + span.EndMarker.Length - first)

                                records.Add(subjectRecord "span" span.Path (utf8.GetBytes(content)))
                            | Ok _, Ok _ -> errors.Add("Review span markers are reversed or empty.")
                            | Error message, _
                            | _, Error message -> errors.Add(message)
                        with error ->
                            errors.Add(error.GetType().Name + ": " + error.Message)

        review.WholeFiles |> List.iter addWhole
        review.MarkedSpans |> List.iter addSpan

        if review.WholeFiles.IsEmpty && review.MarkedSpans.IsEmpty then
            errors.Add(
                "A semantic review must register at least one implementation or test subject."
            )

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            records |> Seq.sort |> String.concat "" |> Repository.sha256Text |> Ok

    let verify (root: RepositoryRoot) (declarations: ContractDeclaration list) (today: DateOnly) =
        match ReviewRegistry.load root with
        | Error message -> Error [ Diagnostic.create DiagnosticCode.InvalidReview message ]
        | Ok reviews ->
            let errors = ResizeArray<Diagnostic>()
            let reviewIds = reviews |> List.map _.ContractId

            if reviewIds.Length <> (reviewIds |> Set.ofList |> Set.count) then
                errors.Add(
                    Diagnostic.create
                        DiagnosticCode.InvalidReview
                        "Contract reviews contain duplicate IDs."
                )

            let declaredIds = declarations |> List.map _.Id |> Set.ofList

            if Set.ofList reviewIds <> declaredIds then
                errors.Add(
                    Diagnostic.create
                        DiagnosticCode.InvalidReview
                        "Contract reviews do not exactly cover declared contract IDs."
                )

            for review in reviews do
                if review.ReviewedOn > today then
                    errors.Add(
                        Diagnostic.create
                            DiagnosticCode.InvalidReview
                            $"Review '{review.ContractId}' is future-dated."
                    )

                match declarations |> List.tryFind (fun item -> item.Id = review.ContractId) with
                | None -> ()
                | Some declaration ->
                    match subjectHash root declaration review with
                    | Error found ->
                        found
                        |> List.iter (fun message ->
                            errors.Add(Diagnostic.create DiagnosticCode.InvalidReview message))
                    | Ok actual when actual <> review.ReviewSubjectHash ->
                        errors.Add(
                            Diagnostic.create
                                DiagnosticCode.InvalidReview
                                ($"Review '{review.ContractId}' is stale; its current subject hash is "
                                 + $"{actual}. Re-review the exact heading and assertion sources "
                                 + "before recording it.")
                        )
                    | Ok _ -> ()

            if errors.Count = 0 then
                Ok reviews
            else
                Error(List.ofSeq errors)
