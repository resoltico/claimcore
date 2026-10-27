module ClaimCore.AcceptanceTests.CliUsageTests

open System
open System.IO
open System.Text
open System.Collections.Generic
open Expecto
open ClaimCore.Contract
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
            Message = "unknownOption"
            Values = [ "flag", "--expected-revison" ]
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
            Values = [ "flag", "--registration"; "value", "swap"; "allowed", "keep|replace" ]
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
            "unknownCommand", "Unknown command: «command»."
            "unknownOption", "Unknown option: «flag»."
            "suggestion", "Did you mean «suggestion»?"
            "missingOption", "Missing option: «flag»."
            "missingOperand", "Missing operand: «operand»."
            "extraOperand", "Unexpected operand: «operand»."
            "invalidInteger", "Invalid whole-number value «value» for «flag»."
            "invalidChoice", "Invalid value «value» for «flag»; expected «allowed»."
            "notApplicable", "«flag» does not apply with «because»."
            "duplicateOption", "Option supplied more than once: «flag»."
            "missingValue", "Missing value for «flag»."
            "helpTopic", "Unknown help topic."
            "helpOptions",
            "Help accepts one topic and one supported language; --format is not available."
            "describeSelector",
            "Unknown operation to describe. Omit the selector to list operations."
            "describeOptions",
            "Invalid describe options. Use --format text|json; --language is available only with text output."
            "schemaSelector",
            "Unknown schema selection. Use a schema name or an endpoint followed by request or response."
            "schemaOptions", "Schema accepts a name or an endpoint and direction, without options."
            "versionOptions", "Version accepts only --json."
            "invocation",
            "Invalid invocation of «command». This command accepts no options or operands."
        ]
        "lv",
        [
            "unknownCommand", "Nezināma komanda: «command»."
            "unknownOption", "Nezināma opcija: «flag»."
            "suggestion", "Vai bija domāts «suggestion»?"
            "missingOption", "Trūkst opcijas: «flag»."
            "missingOperand", "Trūkst argumenta: «operand»."
            "extraOperand", "Negaidīts arguments: «operand»."
            "invalidInteger", "Nederīga vesela skaitļa vērtība «value» opcijai «flag»."
            "invalidChoice", "Nederīga vērtība «value» opcijai «flag»; jābūt «allowed»."
            "notApplicable", "«flag» nav piemērojams kopā ar «because»."
            "duplicateOption", "Opcija norādīta vairākas reizes: «flag»."
            "missingValue", "Trūkst vērtības opcijai «flag»."
            "helpTopic", "Nezināma palīdzības tēma."
            "helpOptions",
            "Palīdzībai norādiet vienu tēmu un vienu atbalstītu valodu; --format nav pieejams."
            "describeSelector",
            "Nezināma aprakstāmā darbība. Nenorādiet atlasi, lai redzētu visas darbības."
            "describeOptions",
            "Nederīgas describe opcijas. Izmantojiet --format text|json; --language ir pieejama tikai teksta izvadei."
            "schemaSelector",
            "Nezināma shēmas atlase. Norādiet shēmas nosaukumu vai galapunktu un request vai response."
            "schemaOptions", "Schema pieņem nosaukumu vai galapunktu un virzienu bez opcijām."
            "versionOptions", "Version pieņem tikai --json."
            "invocation", "Nederīgs «command» izsaukums. Šī komanda nepieņem opcijas vai operandus."
        ]
        "ar",
        [
            "unknownCommand", "أمر غير معروف: «command»."
            "unknownOption", "خيار غير معروف: «flag»."
            "suggestion", "هل تقصد «suggestion»؟"
            "missingOption", "الخيار مفقود: «flag»."
            "missingOperand", "المعامل مفقود: «operand»."
            "extraOperand", "معامل غير متوقع: «operand»."
            "invalidInteger", "قيمة عدد صحيح غير صالحة «value» للخيار «flag»."
            "invalidChoice", "قيمة غير صالحة «value» للخيار «flag»؛ المتوقع «allowed»."
            "notApplicable", "لا ينطبق «flag» مع «because»."
            "duplicateOption", "تم تقديم الخيار أكثر من مرة: «flag»."
            "missingValue", "قيمة الخيار «flag» مفقودة."
            "helpTopic", "موضوع المساعدة غير معروف."
            "helpOptions", "تقبل المساعدة موضوعًا واحدًا ولغة مدعومة واحدة؛ الخيار --format غير متاح."
            "describeSelector", "العملية المطلوب وصفها غير معروفة. احذف المحدد لعرض العمليات."
            "describeOptions",
            "خيارات describe غير صالحة. استخدم --format text|json؛ يتاح --language مع الإخراج النصي فقط."
            "schemaSelector",
            "اختيار المخطط غير معروف. استخدم اسم مخطط أو نقطة نهاية تتبعها request أو response."
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

        for language in LocalizationPolicy.languages do
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

            if example.Message <> "unknownOption" then
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
        "help topic", [ "help"; "unknown-topic" ], "helpTopic", "help [topic]"
        "help options", [ "help"; "--format"; "json" ], "helpOptions", "help [topic]"
        "describe selector",
        [ "describe"; "case"; "bad" ],
        "describeSelector",
        "describe [family verb]"
        "describe options",
        [ "describe"; "--format"; "json" ],
        "describeOptions",
        "describe [family verb]"
        "schema selector", [ "schema"; "unknown-schema" ], "schemaSelector", "schema name"
        "schema options", [ "schema"; "invocation" ], "schemaOptions", "schema name"
        "version options", [ "version"; "--bad" ], "versionOptions", "version [--json]"
        "call invocation", [ "call"; "--retry" ], "invocation", "call"
        "session invocation", [ "session"; "extra" ], "invocation", "session"
    ]

let private qualifyHost (arguments, key, syntax) () =
    let directory = Directory.CreateTempSubdirectory("claimcore-cli-host-usage-")

    try
        let root = RepositoryRoot.find ()
        let cli = PublishedProcesses.publish root directory.FullName "ClaimCore.Cli"

        for language in
            (if key = "schemaSelector" then
                 [ "en" ]
             else
                 LocalizationPolicy.languages) do
            let result =
                ProcessRunner.dotnet
                    8_000
                    cli
                    (if key = "schemaSelector" then
                         arguments
                     else
                         arguments @ [ "--language"; language ])
                    (Dictionary<string, string>())
                    None

            Expect.equal result.ExitCode 64 "Offline refusal before configuration or stdin"
            Expect.isEmpty result.StandardOutput "No request or reply is emitted"
            let text = Encoding.UTF8.GetString result.StandardError

            let values =
                if key = "invocation" then
                    [ "command", List.head arguments ]
                else
                    []

            Expect.stringContains text (expected language key values) "Specific localized problem"

            Expect.stringContains text ("claimcore " + syntax) "Correct host-owned syntax"
            Expect.isLessThanOrEqual (text.TrimEnd().Split('\n').Length) 3 "Concise host guidance"

            for leaked in [ "Invalid Topic"; "Nederīgs Topic"; "Selector"; "Options"; "Invocation" ] do
                Expect.isFalse
                    (text.Contains(leaked, StringComparison.Ordinal))
                    "No internal enum names"
    finally
        directory.Delete true

[<Tests>]
let hostTests =
    hostExamples
    |> List.map (fun (name, arguments, key, syntax) ->
        testCase name (qualifyHost (arguments, key, syntax)))
    |> testList "published CLI host usage errors"
    |> testSequenced
