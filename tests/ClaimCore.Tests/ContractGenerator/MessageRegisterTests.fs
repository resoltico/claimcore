module ClaimCore.Tests.MessageRegisterTests

open System
open Expecto
open ClaimCore.ProtocolGeneration

let private update language key text source =
    source
    |> Map.change
        language
        (Option.map (List.map (fun (name, value) -> name, if name = key then text else value)))

let private rejects fragment action =
    let mutable message = ""

    try
        action ()
    with :? InvalidOperationException as error ->
        message <- error.Message

    Expect.stringContains
        message
        fragment
        "The intended register or glossary guard rejects the control"

[<Tests>]
let tests =
    testList
        "message language registers"
        [
            testCase "the real catalogs satisfy register and glossary checks" (fun () ->
                CatalogValidation.validate () |> ignore)
            testCase "wire hints are refused in every language" (fun () ->
                for language in [ "en"; "lv"; "ar" ] do
                    AuthoredMessages.all ()
                    |> update language "field.incidentDate.hint" "Use YYYY-MM-DD."
                    |> fun source ->
                        rejects "Wire-format text" (fun () -> MessageRegisters.validateCopy source))
            testCase "a repeated sentence is refused without relying on wire tokens" (fun () ->
                let source = AuthoredMessages.all ()
                let rule = source["en"] |> List.find (fst >> (=) "field.status.rule") |> snd

                source
                |> update "en" "field.status.hint" rule
                |> fun source ->
                    rejects "Repeated sentence" (fun () -> MessageRegisters.validateCopy source))
            testCase
                "missing all-language hint and malformed GUI arguments stop publication"
                (fun () ->
                    let source = AuthoredMessages.all ()

                    let missing =
                        source
                        |> Map.map (fun _ entries ->
                            entries |> List.filter (fst >> ((<>) "field.claimantName.hint")))

                    rejects "Unknown core presentation identity" (fun () ->
                        CatalogValidation.validateCatalogs missing |> ignore)

                    let changed =
                        source
                        |> Map.map (fun _ entries ->
                            entries
                            |> List.map (fun (k, v) ->
                                k,
                                if k = "diagnostic.CALENDAR_DATE_REQUIRED.gui" then
                                    "Enter {unexpected}."
                                else
                                    v))

                    rejects "Message producer arguments differ" (fun () ->
                        CatalogValidation.validateCatalogs changed |> ignore))
            testCase "forbidden case terms are refused across core chrome and CLI" (fun () ->
                for family in [ "field."; "ui."; "cli." ] do
                    let source = AuthoredMessages.all ()

                    let key =
                        source["ar"]
                        |> List.find (fun (k, _) -> k.StartsWith(family, StringComparison.Ordinal))
                        |> fst

                    source
                    |> update "ar" key "القضايا"
                    |> fun source ->
                        rejects "Forbidden glossary variant" (fun () ->
                            MessageRegisters.validate source))
        ]
