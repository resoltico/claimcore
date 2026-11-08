module ClaimCore.AcceptanceTests.CliUsageTests

open System
open System.IO
open System.Text
open System.Collections.Generic
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.TestSupport

type private Example =
    {
        Name: string
        Arguments: string list
        Message: string
        Values: (string * string) list
        Suggestion: string option
    }

let private close = [ "case"; "close"; "REF"; "--expected-revision"; "1" ]

let private correct =
    [
        "case"
        "correct"
        "REF"
        "--expected-revision"
        "1"
        "--registration"
        "keep"
        "--decision"
        "keep"
        "--payment"
        "keep"
    ]

let private examples =
    [
        {
            Name = "missing revision"
            Arguments = [ "case"; "close"; "REF" ]
            Message = "missingOption"
            Values = [ "flag", "--expected-revision" ]
            Suggestion = None
        }
        {
            Name = "misspelled option"
            Arguments = [ "case"; "close"; "REF"; "--expected-revison"; "1" ]
            Message = "unknownOptionFor"
            Values = [ "flag", "--expected-revison"; "command", "case close" ]
            Suggestion = Some "--expected-revision"
        }
        {
            Name = "misspelled verb"
            Arguments = [ "case"; "closee"; "REF" ]
            Message = "unknownCommand"
            Values = [ "command", "case closee" ]
            Suggestion = Some "claimcore case close"
        }
        {
            Name = "missing reference"
            Arguments = [ "case"; "close"; "--expected-revision"; "1" ]
            Message = "missingOperand"
            Values = [ "operand", "CASE-REFERENCE" ]
            Suggestion = None
        }
        {
            Name = "extra operand"
            Arguments = close @ [ "extra" ]
            Message = "extraOperand"
            Values = [ "operand", "extra" ]
            Suggestion = None
        }
        {
            Name = "invalid integer"
            Arguments = [ "case"; "close"; "REF"; "--expected-revision"; "three" ]
            Message = "invalidInteger"
            Values = [ "flag", "--expected-revision"; "value", "three" ]
            Suggestion = None
        }
        {
            Name = "invalid choice"
            Arguments =
                [
                    "case"
                    "correct"
                    "REF"
                    "--expected-revision"
                    "1"
                    "--registration"
                    "swap"
                    "--decision"
                    "keep"
                    "--payment"
                    "keep"
                ]
            Message = "invalidChoice"
            Values = [ "flag", "--registration"; "value", "swap"; "allowed", "keep|update" ]
            Suggestion = None
        }
        {
            Name = "inapplicable payment input"
            Arguments = correct @ [ "--payment-date"; "2026-01-01" ]
            Message = "notApplicable"
            Values = [ "flag", "--payment-date"; "because", "--payment keep" ]
            Suggestion = None
        }
        {
            Name = "duplicate option"
            Arguments = close @ [ "--expected-revision"; "2" ]
            Message = "duplicateOption"
            Values = [ "flag", "--expected-revision" ]
            Suggestion = None
        }
        {
            Name = "dangling option"
            Arguments = [ "case"; "close"; "REF"; "--expected-revision" ]
            Message = "missingValue"
            Values = [ "flag", "--expected-revision" ]
            Suggestion = None
        }
    ]

let private samples =
    [
        "en",
        [
            "unknownCommand", "Unknown command \"«command»\"."
            "unknownOptionFor", "Unknown option \"«flag»\" for \"«command»\"."
            "suggestion", "Did you mean «suggestion»?"
            "missingOption", "Missing option: «flag»."
            "missingOperand", "Missing operand: «operand»."
            "extraOperand", "Unexpected operand: «operand»."
            "invalidInteger", "Invalid whole-number value «value» for «flag»."
            "invalidChoice", "Invalid value «value» for «flag»; expected «allowed»."
            "notApplicable", "«flag» does not apply with «because»."
            "duplicateOption", "Option supplied more than once: «flag»."
            "missingValue", "Missing value for «flag»."
            "helpOptions",
            "Help accepts one topic and one supported language; --format is not available."
            "describeOptions",
            "Invalid describe options. Use --format text|json; --language is available only with text output."
            "schemaOptions", "Schema accepts a name or an endpoint and direction, without options."
            "versionOptions", "Version accepts only --json."
            "invocation",
            "Invalid invocation of «command». This command accepts no options or operands."
        ]
        "lv",
        [
            "unknownCommand", "Nezināma komanda \"«command»\"."
            "unknownOptionFor", "Nezināma opcija \"«flag»\" komandai \"«command»\"."
            "suggestion", "Vai bija domāts «suggestion»?"
            "missingOption", "Trūkst opcijas: «flag»."
            "missingOperand", "Trūkst argumenta: «operand»."
            "extraOperand", "Negaidīts arguments: «operand»."
            "invalidInteger", "Nederīga vesela skaitļa vērtība «value» opcijai «flag»."
            "invalidChoice", "Nederīga vērtība «value» opcijai «flag»; jābūt «allowed»."
            "notApplicable", "«flag» nav piemērojams kopā ar «because»."
            "duplicateOption", "Opcija norādīta vairākas reizes: «flag»."
            "missingValue", "Trūkst vērtības opcijai «flag»."
            "helpOptions",
            "Palīdzībai norādiet vienu tēmu un vienu atbalstītu valodu; --format nav pieejams."
            "describeOptions",
            "Nederīgas describe opcijas. Izmantojiet --format text|json; --language ir pieejama tikai teksta izvadei."
            "schemaOptions", "Schema pieņem nosaukumu vai galapunktu un virzienu bez opcijām."
            "versionOptions", "Version pieņem tikai --json."
            "invocation", "Nederīgs «command» izsaukums. Šī komanda nepieņem opcijas vai operandus."
        ]
        "ar",
        [
            "unknownCommand", "أمر غير معروف \"«command»\"."
            "unknownOptionFor", "خيار غير معروف \"«flag»\" للأمر \"«command»\"."
            "suggestion", "هل تقصد «suggestion»؟"
            "missingOption", "الخيار مفقود: «flag»."
            "missingOperand", "المعامل مفقود: «operand»."
            "extraOperand", "معامل غير متوقع: «operand»."
            "invalidInteger", "قيمة عدد صحيح غير صالحة «value» للخيار «flag»."
            "invalidChoice", "قيمة غير صالحة «value» للخيار «flag»؛ المتوقع «allowed»."
            "notApplicable", "لا ينطبق «flag» مع «because»."
            "duplicateOption", "تم تقديم الخيار أكثر من مرة: «flag»."
            "missingValue", "قيمة الخيار «flag» مفقودة."
            "helpOptions", "تقبل المساعدة موضوعًا واحدًا ولغة مدعومة واحدة؛ الخيار --format غير متاح."
            "describeOptions",
            "خيارات describe غير صالحة. استخدم --format text|json؛ يتاح --language مع الإخراج النصي فقط."
            "schemaOptions", "يقبل schema اسمًا أو نقطة نهاية واتجاهًا، دون خيارات."
            "versionOptions", "يقبل version الخيار --json فقط."
            "invocation", "استدعاء «command» غير صالح. لا يقبل هذا الأمر خيارات أو معاملات."
        ]
    ]
    |> List.map (fun (language, entries) -> language, Map.ofList entries)
    |> Map.ofList

// Literal expectations for these published-process scenarios; no renderer generates them.
let private expected language key values =
    (samples[language][key], values)
    ||> List.fold (fun text (name, value) -> text.Replace("«" + name + "»", value))

let private qualify example () =
    // Each case owns its publication. The sequential group avoids shared producer writes.
    let directory = Directory.CreateTempSubdirectory("claimcore-cli-usage-")

    try
        let root = RepositoryRoot.find ()
        let cli = PublishedProcesses.publish root directory.FullName "ClaimCore.Cli"

        // These full error oracles are independently authored for EN/LV/AR.
        // The separate publication smoke test covers every supported language.
        for KeyValue(language, _) in samples do
            let result =
                ProcessRunner.dotnet
                    8_000
                    cli
                    (example.Arguments @ [ "--language"; language ])
                    (Dictionary<string, string>())
                    None

            Expect.equal result.ExitCode 64 "Usage refuses before configuration or dispatch"
            Expect.equal result.StandardOutput.Length 0 "Invalid input emits no request/reply"
            let text = Encoding.UTF8.GetString result.StandardError

            Expect.stringContains
                text
                (expected language example.Message example.Values)
                "The specific localized error agrees with independent literal text"

            example.Suggestion
            |> Option.iter (fun suggestion ->
                Expect.stringContains
                    text
                    (expected language "suggestion" [ "suggestion", suggestion ])
                    "Relevant spelling suggestion")

            let lines = text.TrimEnd().Split('\n')
            Expect.isLessThanOrEqual lines.Length 8 "Concise guidance"

            if example.Message <> "unknownCommand" then
                Expect.stringContains
                    lines[lines.Length - 1]
                    ("claimcore case " + example.Arguments[1])
                    "Only the matched synopsis follows the errors"

            if example.Message <> "unknownOptionFor" then
                Expect.equal lines.Length 2 "One problem and one synopsis/suggestion"
    finally
        directory.Delete true

[<Tests>]
let tests =
    examples
    |> List.map (fun example -> testCase example.Name (qualify example))
    |> testList "published CLI usage errors"
    |> testSequenced

let private hostExamples =
    [
        "help topic",
        [ "help"; "unknown-topic" ],
        "unknownCommand",
        [ "command", "unknown-topic" ],
        None
        "help options",
        [ "help"; "--format"; "json" ],
        "helpOptions",
        [],
        Some "help [topic | family [verb...]]"
        "describe selector",
        [ "describe"; "case"; "bad" ],
        "unknownCommand",
        [ "command", "case bad" ],
        None
        "describe options",
        [ "describe"; "--format"; "json" ],
        "describeOptions",
        [],
        Some "describe [family verb]"
        "schema selector",
        [ "schema"; "unknown-schema" ],
        "unknownCommand",
        [ "command", "unknown-schema" ],
        None
        "schema options", [ "schema"; "invocation" ], "schemaOptions", [], Some "schema name"
        "version options", [ "version"; "--bad" ], "versionOptions", [], Some "version [--json]"
        "call invocation", [ "call"; "--retry" ], "invocation", [ "command", "call" ], Some "call"
        "session invocation",
        [ "session"; "extra" ],
        "invocation",
        [ "command", "session" ],
        Some "session"
    ]

let private qualifyHost (arguments, key, values, syntax) () =
    let directory = Directory.CreateTempSubdirectory("claimcore-cli-host-usage-")

    try
        let root = RepositoryRoot.find ()
        let cli = PublishedProcesses.publish root directory.FullName "ClaimCore.Cli"

        for KeyValue(language, _) in samples do
            let schemaSelector = List.head arguments = "schema" && key = "unknownCommand"
            let environment = Dictionary<string, string>()

            if schemaSelector then
                environment["CLAIMCORE_LANGUAGE"] <- language

            let result =
                ProcessRunner.dotnet
                    8_000
                    cli
                    (if schemaSelector then
                         arguments
                     else
                         arguments @ [ "--language"; language ])
                    environment
                    None

            Expect.equal result.ExitCode 64 "Offline refusal before configuration or stdin"
            Expect.isEmpty result.StandardOutput "No request or reply is emitted"
            let text = Encoding.UTF8.GetString result.StandardError

            Expect.stringContains
                text
                (expected language key values)
                "Specific localized problem and original token"

            match syntax with
            | Some syntax ->
                Expect.stringContains text ("claimcore " + syntax) "Correct host-owned syntax"

                Expect.isLessThanOrEqual
                    (text.TrimEnd().Split('\n').Length)
                    3
                    "Concise host guidance"
            | None ->
                Expect.stringContains
                    text
                    "claimcore help <family>"
                    "Unknown selectors retain the family repair route"

                for family in [ "case"; "authority"; "work"; "reminder"; "operation"; "context" ] do
                    Expect.stringContains
                        text
                        ("\n" + family + " — ")
                        "Complete human family index remains available"

                Expect.isFalse
                    (text.Contains "Unknown command: .")
                    "Unknown selector is never omitted"

            for leaked in [ "Invalid Topic"; "Nederīgs Topic"; "Selector"; "Options"; "Invocation" ] do
                Expect.isFalse
                    (text.Contains(leaked, StringComparison.Ordinal))
                    "No internal enum names"
    finally
        directory.Delete true

[<Tests>]
let hostTests =
    hostExamples
    |> List.map (fun (name, arguments, key, values, syntax) ->
        testCase name (qualifyHost (arguments, key, values, syntax)))
    |> testList "published CLI host usage errors"
    |> testSequenced

[<Tests>]
let localizedDiscovery =
    testCase "published help and operation discovery render every supported language" (fun () ->
        // Hand-authored literals keep these process checks independent of catalog lookups.
        let languages =
            [
                "en", "Case facts and decisions", "Open a case", "Claimed amount"
                "lv", "Lietu fakti un lēmumi", "Atvērt lietu", "Pieprasītā summa"
                "ar", "وقائع الملفات والقرارات", "فتح ملف", "المبلغ المطالب به"
                "ko", "청구 건 정보 및 결정", "청구 건 등록", "청구 금액"
                "ja", "案件情報と決定", "案件を登録", "請求額"
                "zh-Hans", "案件资料与决定", "新建理赔案件", "索赔金额"
                "zh-Hant", "案件資料與決定", "新建理賠案件", "索賠金額"
                "es",
                "Datos del expediente y decisiones",
                "Abrir un expediente",
                "Importe reclamado"
                "de", "Falldaten und Entscheidungen", "Fall eröffnen", "Geforderter Betrag"
            ]

        Expect.equal
            (languages |> List.map (fun (language, _, _, _) -> language) |> Set.ofList)
            (LocalizationPolicy.languages |> Set.ofList)
            "Every real language has independent help and description samples"

        let directory =
            Directory.CreateTempSubdirectory("claimcore-cli-localized-discovery-")

        try
            let root = RepositoryRoot.find ()
            let cli = PublishedProcesses.publish root directory.FullName "ClaimCore.Cli"

            for language, family, act, amount in languages do
                for arguments, literals in
                    [
                        [ "help"; "--language"; language ], [ family; "claimcore help" ]
                        [ "describe"; "case"; "open"; "--format"; "text"; "--language"; language ],
                        [ act; amount; "claimcore case open"; "--claimed-amount" ]
                    ] do
                    let result =
                        ProcessRunner.dotnet
                            8_000
                            cli
                            arguments
                            (Dictionary<string, string>())
                            None

                    Expect.equal result.ExitCode 0 (language + " discovery succeeds offline")
                    Expect.isEmpty result.StandardError (language + " discovery emits no errors")
                    let text = Encoding.UTF8.GetString result.StandardOutput

                    for literal in literals do
                        Expect.stringContains
                            text
                            literal
                            (language + " independent language sample")

                    for key in [ "cli.family.case"; "act.OPEN.label"; "field.claimedAmount.label" ] do
                        Expect.isFalse
                            (text.Contains(key, StringComparison.Ordinal))
                            (language + " discovery never exposes untranslated keys")
        finally
            directory.Delete true)

[<Tests>]
let closedFlagsBeforeInput =
    testCase "all published verbs reject undeclared flags while stdin remains open" (fun () ->
        let directory = Directory.CreateTempSubdirectory("claimcore-closed-cli-flags-")

        try
            let root = RepositoryRoot.find ()
            let cli = PublishedProcesses.publish root directory.FullName "ClaimCore.Cli"

            let captured =
                File.ReadAllLines(Path.Combine(root, "tests/fixtures/cli-commands.txt"))
                |> Array.map (fun row ->
                    row.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
                |> Array.toList

            let host =
                [
                    [ "help" ]
                    [ "version" ]
                    [ "describe" ]
                    [ "schema"; "invocation" ]
                    [ "call" ]
                    [ "session" ]
                    [ "shell" ]
                    [ "configure" ]
                ]

            for index, arguments in List.indexed (captured @ host) do
                // Only closed command words are logged; the index identifies the complete fixture.
                let words = arguments |> List.truncate 2 |> String.concat " "
                let command = words.Substring(0, min 80 words.Length)
                let flag = "--undeclared-flag-" + string index

                let context =
                    $"Closed CLI syntax probe index={index}; command={command}; flag={flag}."

                let start =
                    System.Diagnostics.ProcessStartInfo(
                        "dotnet",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    )

                CliProcessEnvironment.clearInherited start
                CliProcessEnvironment.isolateProfile start directory.FullName
                start.ArgumentList.Add cli

                for argument in arguments @ [ flag ] do
                    start.ArgumentList.Add argument
                // None deliberately leaves the redirected writer open: reading stdin would block.
                let result =
                    try
                        BoundedProcess.run start None 1_048_576 8_000
                    with :? BoundedProcessFailure as error ->
                        raise (InvalidOperationException(context + " " + error.Message, error))

                Expect.equal
                    result.ExitCode
                    64
                    (context + " Every verb refuses the unknown flag before stdin or dispatch")

                Expect.isEmpty
                    result.StandardOutput
                    (context + " Refused syntax emits no request or reply")
        finally
            directory.Delete true)
