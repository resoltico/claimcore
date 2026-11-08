module ClaimCore.Tests.RuntimeCatalogTests

open System.Text
open Expecto
open ClaimCore.Cli
open ClaimCore.Presentation

[<Tests>]
let tests =
    testList
        "CLI authored runtime catalogs"
        [
            testCase "startup reads authored English Latvian and Arabic resources" (fun () ->
                for language, expected in
                    [ "en", "Open a case"; "lv", "Atvērt lietu"; "ar", "فتح ملف" ] do
                    let actual = MessageCatalogs.render language "act.OPEN.label" Map.empty

                    Expect.equal
                        actual
                        expected
                        "The authored resource reaches CLI rendering without a generated runtime directory")
            testCase "stale Latvian falls back through the startup catalog admission" (fun () ->
                let english = Encoding.UTF8.GetBytes "{\"greeting\":\"Current English\"}"

                let translated =
                    Encoding.UTF8.GetBytes
                        "{\"greeting\":{\"text\":\"STALE_CANARY\",\"source\":\"000000000000\"}}"

                let catalog = MessageCatalogs.catalogFrom "lv" [ english ] [ translated ]

                Expect.equal
                    (Messages.render catalog "greeting" Map.empty)
                    "Current English"
                    "No generator repairs or resolves these runtime inputs")
        ]
