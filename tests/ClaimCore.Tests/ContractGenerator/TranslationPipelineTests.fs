module ClaimCore.Tests.TranslationPipelineTests

open Expecto
open ClaimCore.ContractGeneration
open ClaimCore.Presentation

[<Tests>]
let tests =
    testList
        "translation source hashes and fallback"
        [
            testCase "English text is hashed as UTF-8 with a twelve-digit prefix" (fun () ->
                Expect.equal
                    (LocalizationPolicy.sourceHash "hello")
                    "2cf24dba5fb0"
                    "SHA-256 published hello vector.")
            testCase "stale and absent translations produce English at load time" (fun () ->
                let english = Map.ofList [ "greeting", "Hello {name}"; "missing", "Missing" ]

                let translated =
                    Map.ofList
                        [
                            "greeting",
                            {
                                LocalizationPolicy.Translation.Text = "Sveiki {name}"
                                LocalizationPolicy.Translation.Source = "000000000000"
                            }
                        ]

                let resolved, issues = LocalizationPolicy.resolve "lv" english translated

                Expect.equal
                    resolved
                    english
                    "Neither stale nor missing text reaches the consumer."

                Expect.equal
                    (issues |> List.map (fun issue -> issue.Key, issue.Reason))
                    [ "greeting", "stale"; "missing", "missing" ]
                    "Report both causes.")
            testCase "a current translation retains its authored text" (fun () ->
                let english = Map.ofList [ "greeting", "Hello {name}" ]

                let translated =
                    Map.ofList
                        [
                            "greeting",
                            {
                                LocalizationPolicy.Translation.Text = "Sveiki {name}"
                                LocalizationPolicy.Translation.Source =
                                    LocalizationPolicy.sourceHash "Hello {name}"
                            }
                        ]

                let resolved, issues = LocalizationPolicy.resolve "lv" english translated

                Expect.equal
                    resolved["greeting"]
                    "Sveiki {name}"
                    "A current source hash admits the translation."

                Expect.isEmpty issues "No fallback is needed.")
            testCase "optional missing translations warn while required ones gate" (fun () ->
                let english = Map.ofList [ "greeting", "Hello" ]
                let _, optional = LocalizationPolicy.resolve "ja" english Map.empty
                let _, required = LocalizationPolicy.resolve "ar" english Map.empty

                Expect.isEmpty
                    (AuthoredMessages.required optional)
                    "Optional coverage does not gate."

                Expect.equal
                    (AuthoredMessages.required required).Length
                    1
                    "Required coverage gates.")
            testCase "only English Latvian and Arabic gate translations" (fun () ->
                Expect.equal
                    (LocalizationPolicy.languageOptions
                     |> List.filter _.required
                     |> List.map _.value)
                    [ "en"; "lv"; "ar" ]
                    "Owner-adopted required languages.")
        ]
