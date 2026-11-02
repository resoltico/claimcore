module ClaimCore.Tests.LocalizationCatalogTests

open Expecto
open ClaimCore.TestSupport
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.ProtocolGeneration
open ClaimCore.Cli

let private change language transform =
    AuthoredMessages.all () |> Map.change language (Option.map transform)

let private invalidCatalogs () =
    for language, transform in
        [
            "lv", List.filter (fst >> (<>) "ui.cases")
            "ar",
            List.map (fun (key, value) ->
                key, if key = "ui.cases" then value + " {extra}" else value)
            "en",
            List.map (fun (key, value) ->
                key,
                if key = "ui.cases" then
                    "{count, plural, one {case} other {cases}}"
                else
                    value)
        ] do
        Expect.throws
            (fun () -> change language transform |> CatalogValidation.validateCatalogs |> ignore)
            "Generation rejects broken language/argument/grammar contracts"

let private sameMutationEveryLanguage key transform =
    AuthoredMessages.all ()
    |> Map.map (fun _ entries ->
        entries
        |> List.map (fun (name, value) -> name, if name = key then transform value else value))

let private producerMutations () =
    let producers =
        [
            "cli.usage.invalidInteger", "value"
            "cli.delivery.unresolved", "statusCommand"
            "cli.help.call", "maximumRequestBytes"
            "cli.help.configuration", "maximumTrustRootBytes"
            "cli.help.layout", "body"
        ]

    for key, argument in producers do
        for change in
            [
                (fun value -> value + " {unexpected}")
                (fun value -> value.Replace("{" + argument + "}", ""))
                (fun value -> value.Replace("{" + argument + "}", "{renamed}"))
            ] do
            Expect.throws
                (fun () ->
                    sameMutationEveryLanguage key change
                    |> CatalogValidation.validateCatalogs
                    |> ignore)
                ("Generator must reject changed actual producer arguments even with translation parity: "
                 + key)

    for key in
        [
            "act.OPEN.label"
            "act.OPEN.meaning"
            "act.OPEN.confirmation"
            "field.principal.kind.label"
            "group.registration.meaning"
        ] do
        Expect.throws
            (fun () ->
                sameMutationEveryLanguage key (fun value -> value + " {unexpected}")
                |> CatalogValidation.validateCatalogs
                |> ignore)
            ("Catalog metadata is rendered without arguments: " + key)

let private actualProducers () =
    let catalogs = CatalogValidation.validate ()

    let fixedMessages =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<CliMessage>
        |> Array.filter (fun case -> case.Name <> "Failure")
        |> Array.map (fun case ->
            let values = case.GetFields() |> Array.map (fun _ -> box "producer-value")

            Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, values)
            |> nonNull
            |> unbox<CliMessage>)
        |> Array.toList

    let failures =
        [
            for code in Vocabulary.all CliFailureCode.vocabulary do
                yield CliMessage.Failure(code |> CliFailureCode.toFailure |> ClientFailure.ofFrame)
            yield
                CliMessage.Failure(
                    ClientFailure.ConfigurationIssue(
                        CliSetting.ServiceUrl,
                        ConfigurationIssue.Missing
                    )
                )
        ]

    for message in fixedMessages @ failures do
        let produced = CliMessages.produce message

        for language in LocalizationPolicy.languages do
            let rendered = Template.render (catalogs[language][produced.Key]) produced.Arguments
            Expect.isNonEmpty rendered "Every typed CLI producer renders in every language"

let private structure () =
    let authored = AuthoredMessages.all ()
    let catalogs = CatalogValidation.validateCatalogs authored
    let english = catalogs["en"]

    for language in [ "en"; "lv"; "ar" ] do
        let templates = catalogs[language]

        for prefix in
            [
                "cli."
                "ui."
                "diagnostic."
                "fault."
                "input."
                "field."
                "group."
                "status."
                "lifecycle."
                "role."
                "principalKind."
                "correction."
                "scope."
                "historyDetail."
            ] do
            let keys (catalog: Map<string, Template>) =
                catalog
                |> Map.keys
                |> Seq.filter (fun key -> key.StartsWith(prefix, System.StringComparison.Ordinal))
                |> Set.ofSeq

            Expect.equal
                (keys templates)
                (keys english)
                ("Catalog key parity: " + language + " " + prefix)

        for key, source in authored[language] do
            let template = Template.parse source |> Result.defaultWith failtest

            Expect.equal
                (Template.arguments template)
                (Template.arguments english[key])
                ("Placeholder parity: " + language + " " + key)

    // These are authored expectations, independent of catalog publication and the renderer.
    for language, key, expected in
        [
            "en", "diagnostic.TEXT_REQUIRED", "A non-blank value is required."
            "lv", "diagnostic.TEXT_REQUIRED", "Nepieciešama netukša vērtība."
            "ar", "diagnostic.TEXT_REQUIRED", "يجب إدخال قيمة غير فارغة."
        ] do
        Expect.equal
            (MessageCatalogs.render language key Map.empty)
            expected
            "Literal catalog samples"

[<Tests>]
let independentWording =
    testCase "independent renderer keys may share translations with core keys" (fun () ->
        let catalog =
            Messages.catalog [ "label.core", "Shared text"; "ui.control", "Shared text" ]

        Expect.isOk catalog "Equality of wording does not imply shared ownership")

[<Tests>]
let termRegistry =
    testCase "rendered vocabularies have distinct names and qualified token keys" (fun () ->
        let expected = Inventory.tokens "renderedVocabularies" |> Set.ofList

        Expect.equal
            (Terms.all |> List.map _.Name |> Set.ofList)
            expected
            "Every rendered vocabulary is named"

        let first = Terms.all.Head

        Expect.throws
            (fun () -> Terms.validateRegistry [ first; first ] |> ignore)
            "Duplicate vocabulary names are detected"

        Expect.throws
            (fun () ->
                Terms.validateRegistry
                    [
                        { first with
                            Labels = first.Labels @ first.Labels
                        }
                    ]
                |> ignore)
            "Duplicate tokens within a vocabulary are detected"

        Expect.isNonEmpty
            (Terms.all
             |> List.filter (fun vocabulary ->
                 vocabulary.Labels |> List.exists (fun (token, _) -> token = "CASE")))
            "Equal CASE tokens across scope and subject are valid")

[<Tests>]
let tests =
    testList
        "localization publication"
        [
            testCase "all supported catalogs pass the single generator validator" (fun () ->
                CatalogValidation.validate () |> ignore)
            testCase
                "missing Latvian key, extra Arabic hole and English ICU plural each stop generation"
                invalidCatalogs
            testCase "identical all-language producer mutations stop generation" producerMutations
            testCase "actual CLI producers render with their own argument contracts" actualProducers
            testCase "catalog key sets, placeholders and template grammar remain valid" structure
        ]

[<Tests>]
let unreferencedChrome =
    testCase "renderer catalogs reject unreferenced keys with positive controls" (fun () ->
        let good = AuthoredMessages.all ()
        CatalogValidation.validateCatalogs good |> ignore

        for prefix in [ "ui."; "cli." ] do
            let bad =
                good
                |> Map.map (fun _ entries -> (prefix + "unreferencedControl", "Unused") :: entries)

            Expect.throws
                (fun () -> CatalogValidation.validateCatalogs bad |> ignore)
                "The unused-key negative control is detected")

[<Tests>]
let unknownMessageFamily =
    testCase "message-family ownership rejects an unregistered core namespace" (fun () ->
        let good = AuthoredMessages.all ()
        CatalogValidation.validateCatalogs good |> ignore

        let bad =
            good
            |> Map.map (fun _ entries ->
                ("unregisteredFamily.control", "Independent unknown-family control") :: entries)

        Expect.throws
            (fun () -> CatalogValidation.validateCatalogs bad |> ignore)
            "A core family must have an actual registered owner")
