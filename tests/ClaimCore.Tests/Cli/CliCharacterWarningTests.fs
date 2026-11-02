module ClaimCore.Tests.CliCharacterWarningTests

open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Application.Contracts

open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Json
open ClaimCore.Cli

let private warningCases =
    [
        "Synthetic᠎Name", [ "U+180E" ]
        "Synthetic👩‍💻", [ "U+200D" ]
        "Synthetic\U000E0001Name", [ "U+E0001" ]
        "Synthetic\U000E0020Name", [ "U+E0020" ]
    ]

let private scalarWarnings () =
    for value, expected in warningCases do
        let actual = TextDisplay.warning value
        Expect.equal actual.CodePoints expected "The independent scalar identity is visible"
        Expect.isFalse actual.Truncated "A single distinct warning is complete"

let private ordinaryText () =
    for value in [ "Synthetic Name"; "Synthétic"; "SyntheticName"; "Synthetic😀👩💻" ] do
        let actual = TextDisplay.warning value

        Expect.isEmpty
            actual.CodePoints
            "Plain, combining, private-use and ordinary emoji do not warn"

        Expect.isFalse actual.Truncated "No warning does not imply truncation"

let private warningBudget () =
    let eight = "᠎​‌‍⁠⁡⁢⁣"

    let expected =
        [
            "U+180E"
            "U+200B"
            "U+200C"
            "U+200D"
            "U+2060"
            "U+2061"
            "U+2062"
            "U+2063"
        ]

    let exact = TextDisplay.warning (eight + eight)

    Expect.equal
        exact.CodePoints
        expected
        "Duplicates preserve the first eight distinct points in scalar order"

    Expect.isFalse exact.Truncated "Eight distinct points fit, even with repeated occurrences"
    let over = TextDisplay.warning (eight + "⁤" + eight)

    Expect.equal
        over.CodePoints
        expected
        "The ninth distinct point is outside the bounded annotation"

    Expect.isTrue over.Truncated "A ninth distinct point marks truncation"
    let repeated = TextDisplay.warning (String.replicate 12 "᠎")
    Expect.equal repeated.CodePoints [ "U+180E" ] "Repeated invisible points consume one slot"
    Expect.isFalse repeated.Truncated "Duplicate occurrences do not cause truncation"

let private example name =
    let path =
        Path.Combine(
            ClaimCore.TestSupport.RepositoryRoot.find (),
            "tests/fixtures/wire-response-examples.json"
        )

    use document = JsonDocument.Parse(File.ReadAllBytes path)

    let row =
        document.RootElement.GetProperty("cases").EnumerateArray()
        |> Seq.find (fun row -> row.GetProperty("name").GetString() = name)

    row.GetProperty("endpoint").GetString() |> nonNull,
    JsonNode.Parse(WireWriter.bytes (ClaimCore.TestSupport.PositiveExamples.value row))
    |> nonNull

let private memberPath (path: string list) (root: JsonNode) =
    path |> List.fold (fun (node: JsonNode) key -> node[key] |> nonNull) root

let private currentFields root =
    memberPath [ "outcome"; "data"; "state"; "fields"; "registration" ] root

let private reviewFields side root =
    memberPath
        [
            "outcome"
            "data"
            "review"
            "subject"
            side
            "state"
            "fields"
            "registration"
        ]
        root

let private historyFields root =
    let entry =
        (memberPath [ "outcome"; "data"; "entries" ] root).AsArray()[0] |> nonNull

    memberPath [ "receipt"; "subject"; "snapshot"; "view"; "fields"; "registration" ] entry

let private annotated language value points =
    let annotation =
        MessageCatalogs.render
            language
            "label.characterWarning"
            (Map.ofList [ "characters", String.concat ", " points ])

    value + " [" + annotation + "]"

let private rendered exampleName customize verify =
    let endpoint, document = example exampleName
    customize document

    let value =
        StrictJson.read
            Limits.MaximumResponseBytes
            (Encoding.UTF8.GetBytes(document.ToJsonString()))
        |> Result.defaultWith (fun _ -> failtest "Synthetic frozen reply remains JSON")

    let frame =
        {
            Endpoint = Some(ClaimCore.Tests.CliReplies.endpointId endpoint)
            OperationId = None
            Delivery = DeliveryState.Delivered
            Result =
                FrameResult.Reply(
                    ServiceReply.read (Api.tryFind endpoint |> Option.get) value
                    |> Result.defaultWith (fun _ -> failtest "Frozen DTO")
                )
        }

    let bytes = Frames.encode frame
    let original = Array.copy bytes

    for language in LocalizationPolicy.languages do
        let text = TextReply.bytes language frame |> Encoding.UTF8.GetString
        verify language text
        Expect.isTrue (bytes = original) "Human annotations never mutate the structured reply bytes"

        Expect.isTrue
            (Frames.encode frame = original)
            "Structured frame encoding remains byte-identical"

let private currentWarnings () =
    for value, points in warningCases do
        rendered
            "case.get-response-0"
            (fun root -> (currentFields root)["claimantName"] <- JsonValue.Create value)
            (fun language text ->
                Expect.isTrue
                    (text.Contains(
                        annotated language value points,
                        System.StringComparison.Ordinal
                    ))
                    "Current fields retain the value and show its localized scalar warning")

let private previewWarnings () =
    let before = "Synthetic᠎Before"
    let after = "Synthetic👩‍💻After"

    rendered
        "review-all-fields"
        (fun root ->
            (reviewFields "before" root)["claimantName"] <- JsonValue.Create "Snapshot before"
            (reviewFields "after" root)["claimantName"] <- JsonValue.Create "Snapshot after"

            let changes =
                (memberPath [ "outcome"; "data"; "review"; "subject"; "changes" ] root).AsArray()

            let change =
                changes
                |> Seq.map nonNull
                |> Seq.find (fun value ->
                    (value["field"] |> nonNull).GetValue<string>() = "claimantName")

            change["before"] <- JsonValue.Create before
            change["after"] <- JsonValue.Create after)
        (fun language text ->
            let expected =
                annotated language before [ "U+180E" ]
                + " → "
                + annotated language after [ "U+200D" ]

            Expect.isTrue
                (text.Contains(expected, System.StringComparison.Ordinal))
                "Both differing review sides retain their own value and independent warning")

let private fullHistoryWarnings () =
    for value, points in warningCases do
        rendered
            "history-full-DISCLOSED"
            (fun root -> (historyFields root)["claimantName"] <- JsonValue.Create value)
            (fun language text ->
                Expect.isTrue
                    (text.Contains(
                        annotated language value points,
                        System.StringComparison.Ordinal
                    ))
                    "Full history retains the accepted value and localized warning")

let private humanBudgetAndOrdinaryControl () =
    let values = "Synthetic᠎​‌‍⁠⁡⁢⁣⁤"

    let points =
        [
            "U+180E"
            "U+200B"
            "U+200C"
            "U+200D"
            "U+2060"
            "U+2061"
            "U+2062"
            "U+2063"
            "…"
        ]

    rendered
        "case.get-response-0"
        (fun root -> (currentFields root)["claimantName"] <- JsonValue.Create values)
        (fun language text ->
            Expect.isTrue
                (text.Contains(annotated language values points, System.StringComparison.Ordinal))
                "Human annotation visibly marks the ninth-point truncation"

            Expect.isFalse
                (text.Contains("U+2064", System.StringComparison.Ordinal))
                "Ninth code-point identity is outside the visible annotation budget")

    for value in [ "Synthetic Name"; "Synthétic"; "SyntheticName"; "Synthetic😀👩💻" ] do
        rendered
            "case.get-response-0"
            (fun root -> (currentFields root)["claimantName"] <- JsonValue.Create value)
            (fun language text ->
                let label = MessageCatalogs.render language "field.claimantName.label" Map.empty

                Expect.isTrue
                    (text.Split('\n') |> Array.contains (label + ": " + value))
                    "Ordinary text has no warning suffix")

let private retainedTerminalControls () =
    let value = "retained\u001B[31m\r\n\t\b\u009B\u202Ename"

    let verify _ (text: string) =
        for control in [ '\u001B'; '\r'; '\t'; '\b'; '\u009B'; '\u202E' ] do
            Expect.isFalse
                (text.Contains control)
                "Frozen business data cannot emit terminal instructions"

        Expect.isTrue
            (text.Contains "retained\\u{001B}[31m\\u{000D}\\u{000A}")
            "Embedded breaks are visibly data"

    rendered
        "case.get-response-0"
        (fun root -> (currentFields root)["claimantName"] <- JsonValue.Create value)
        verify

    rendered
        "history-full-DISCLOSED"
        (fun root -> (historyFields root)["claimantName"] <- JsonValue.Create value)
        verify

    rendered
        "review-all-fields"
        (fun root ->
            let changes =
                (memberPath [ "outcome"; "data"; "review"; "subject"; "changes" ] root).AsArray()

            let change =
                changes
                |> Seq.map nonNull
                |> Seq.find (fun row ->
                    (row["field"] |> nonNull).GetValue<string>() = "claimantName")

            change["before"] <- JsonValue.Create value
            change["after"] <- JsonValue.Create "safe")
        verify

let private terminalEscapeProfile () =
    let cases =
        [
            "\u001B[31m", "\\u{001B}[31m"
            "\u001B]0;title\u0007", "\\u{001B}]0;title\\u{0007}"
            "\r\n\t\b\u0000\u007F\u0085\u009B",
            "\\u{000D}\\u{000A}\\u{0009}\\u{0008}\\u{0000}\\u{007F}\\u{0085}\\u{009B}"
            "\u061C\u200E\u200F\u202A\u202B\u202C\u202D\u202E",
            "\\u{061C}\\u{200E}\\u{200F}\\u{202A}\\u{202B}\\u{202C}\\u{202D}\\u{202E}"
            "\u2066\u2067\u2068\u2069\u2028\u2029",
            "\\u{2066}\\u{2067}\\u{2068}\\u{2069}\\u{2028}\\u{2029}"
            "literal\\u{001B}", "literal\\\\u{001B}"
            "العربية\u200C\u200D👩‍💻", "العربية\u200C\u200D👩‍💻"
        ]

    for authored, expected in cases do
        Expect.equal (TerminalText.data authored) expected "Independent terminal encoding profile"

    Expect.equal
        (TerminalText.data (String.replicate 10000 "x"))
        (String.replicate 10000 "x")
        "Long data is not silently truncated"

let private terminalDataBoundaries () =
    let dangerous =
        "subject"
        + string (char 27)
        + "[31m"
        + "\r\n\t\b"
        + string (char 0x85)
        + "\u202Ename"

    let principal =
        PrincipalKey.human "https://issuer.example/" dangerous
        |> Result.defaultWith (fun _ ->
            failtest "Terminal rendering must not tighten identity admission")

    let actor: Actor =
        {
            ActorId =
                ActorId.create (System.Guid.Parse "10000000-0000-4000-8000-000000000003")
                |> Option.get
            Principal = principal
            Enabled = true
        }

    for language in LocalizationPolicy.languages do
        for text in
            [
                TextAuthorityReply.actorRow language actor
                TextReplyFormat.displayed language (Some dangerous)
                UsageText.render language [] [ UsageError.ExtraOperand dangerous ]
            ] do
            Expect.isFalse (text.Contains(char 27)) "Human data must never emit ESC"
            Expect.isFalse (text.Contains('\r')) "Data cannot overwrite a line"
            Expect.isFalse (text.Contains('\b')) "Data cannot erase text"
            Expect.isFalse (text.Contains('\u202E')) "Data cannot reverse its visible identity"

    Expect.equal
        (PrincipalKey.parts principal |> fun (_, _, subject) -> subject)
        dangerous
        "Stored identity remains exact"

[<Tests>]
let tests =
    testList
        "CLI character warning text"
        [
            testCase
                "retained current history and review business text escapes terminal controls"
                retainedTerminalControls
            testCase
                "terminal encoding escapes controls but preserves Arabic and joining"
                terminalEscapeProfile
            testCase
                "terminal data boundaries escape admitted identity and usage controls"
                terminalDataBoundaries
            testCase
                "invisible supplementary and joining scalars have explicit identities"
                scalarWarnings
            testCase "ordinary combining private-use and emoji scalars do not warn" ordinaryText
            testCase
                "annotation has eight distinct scalar slots with truthful truncation"
                warningBudget
            testCase "current case values warn in every language" currentWarnings
            testCase "core-supplied differing preview sides warn in every language" previewWarnings
            testCase "full history accepted values warn in every language" fullHistoryWarnings
            testCase
                "human annotations are bounded and ordinary text stays unannotated"
                humanBudgetAndOrdinaryControl
        ]
