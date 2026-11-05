namespace ClaimCore.ProtocolGeneration

open System
open System.Globalization
open System.Reflection
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open ClaimCore.Protocol

/// Register and glossary checks validate authored catalogs before either renderer receives them.
module MessageRegisters =
    let private normalized (text: string) =
        text.Normalize(NormalizationForm.FormD)
        |> Seq.filter (fun character ->
            CharUnicodeInfo.GetUnicodeCategory character <> UnicodeCategory.NonSpacingMark)
        |> Seq.toArray
        |> String

    let validateGlossary glossary (authored: Map<string, (string * string) list>) =
        use document = JsonDocument.Parse(glossary: string)

        for term in document.RootElement.EnumerateObject() do
            for language in LocalizationPolicy.languages do
                let canonical = term.Value.GetProperty(language).GetString() |> nonNull

                if String.IsNullOrWhiteSpace canonical then
                    invalidOp ("Empty glossary term: " + term.Name)

            for language in term.Value.GetProperty("forbidden").EnumerateObject() do
                if not (Map.containsKey language.Name authored) then
                    invalidOp ("Unknown glossary language: " + language.Name)

                for variant in language.Value.EnumerateArray() do
                    let forbidden = variant.GetString() |> nonNull |> normalized

                    if String.IsNullOrWhiteSpace forbidden then
                        invalidOp "Empty forbidden glossary variant."

                    for key, text in authored[language.Name] do
                        if
                            (normalized text)
                                .Contains(forbidden, StringComparison.OrdinalIgnoreCase)
                        then
                            invalidOp (
                                "Forbidden glossary variant in "
                                + language.Name
                                + " "
                                + key
                                + ": "
                                + forbidden
                            )

    let private glossary () =
        use stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaimCore.Glossary.json")
            |> nonNull

        use reader = new IO.StreamReader(stream)
        reader.ReadToEnd()

    let private sentences text =
        Regex.Split(text, @"[.!?؟]+")
        |> Array.map (fun text -> Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " "))
        |> Array.filter (fun text -> text.Length >= 12)
        |> Set.ofArray

    let private technicalRecordKey (key: string) =
        key.StartsWith("ui.support.", StringComparison.Ordinal)
        || key.StartsWith("ui.recordDetails.", StringComparison.Ordinal)
        || List.contains key [ "label.revisionLabel"; "label.observedAt" ]

    let private roleMeaningKey (key: string) =
        key.StartsWith("role.", StringComparison.Ordinal)
        && key.EndsWith(".meaning", StringComparison.Ordinal)

    let private roleScopeKey (key: string) =
        key = "input.scope.label"
        || roleMeaningKey key
        || key.StartsWith("ui.grant", StringComparison.Ordinal)
        || key.StartsWith("ui.role", StringComparison.Ordinal)

    let validateCopy (authored: Map<string, (string * string) list>) =
        let english = authored["en"] |> Map.ofList
        let wire = @"YYYY|ASCII|UTF(?:-?\d+)?|\\[dDsSwW]|\[0-9"

        for KeyValue(language, entries) in authored do
            let catalog = entries |> Map.ofList

            for key, text in entries do
                if
                    key.EndsWith(".hint", StringComparison.Ordinal)
                    && Regex.IsMatch(text, wire, RegexOptions.IgnoreCase)
                then
                    invalidOp ("Wire-format text in GUI hint: " + language + " " + key)

            for KeyValue(key, hint) in catalog do
                if key.EndsWith(".hint", StringComparison.Ordinal) then
                    let prefix = key[.. key.Length - 6]

                    let other =
                        [ prefix + ".label"; prefix + ".rule" ]
                        |> List.choose (fun key -> Map.tryFind key catalog)

                    if
                        other
                        |> List.exists (fun text ->
                            not (Set.intersect (sentences hint) (sentences text)).IsEmpty)
                    then
                        invalidOp ("Repeated sentence on one field: " + language + " " + prefix)

        let banned =
            @"\b(observation|observed|snapshot|nonacceptance|pinned|YYYY|ASCII|UTF(?:-?\d+)?|revision)\b"

        for KeyValue(key, original) in english do
            let text = Regex.Replace(original, @"\{[A-Za-z][A-Za-z0-9_]*\}", "")

            let diagnostic =
                key.StartsWith("diagnostic.", StringComparison.Ordinal)
                || key.StartsWith("fault.", StringComparison.Ordinal)
                || key.StartsWith("host.", StringComparison.Ordinal)

            let gui =
                key.StartsWith("ui.", StringComparison.Ordinal)
                || key.StartsWith("timeline.event.", StringComparison.Ordinal)
                || key.EndsWith(".label", StringComparison.Ordinal)
                || key.EndsWith(".hint", StringComparison.Ordinal)
                || roleMeaningKey key
                || (diagnostic
                    && (key.EndsWith(".gui", StringComparison.Ordinal)
                        || not (Map.containsKey (key + ".gui") english)))

            if
                gui
                && not (technicalRecordKey key)
                && Regex.IsMatch(text, banned, RegexOptions.IgnoreCase)
            then
                invalidOp ("Implementation terminology in GUI copy: " + key)

            if
                gui
                && not (technicalRecordKey key || roleScopeKey key)
                && Regex.IsMatch(text, @"\bscope\b", RegexOptions.IgnoreCase)
            then
                invalidOp ("Scope outside role or record details: " + key)

    let validate authored =
        validateGlossary (glossary ()) authored
        validateCopy authored
