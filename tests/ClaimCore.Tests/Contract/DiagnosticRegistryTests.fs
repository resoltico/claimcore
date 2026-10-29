module ClaimCore.Tests.DiagnosticRegistryTests

open Expecto
open ClaimCore.TestSupport
open ClaimCore.Protocol

[<Tests>]
let tests =
    testList
        "diagnostic ownership"
        [
            testCase "registry rejects duplicate codes across families" (fun () ->
                let first =
                    {
                        DiagnosticCode.Code = "DUPLICATE"
                        MessageKey = "diagnostic.DUPLICATE"
                        Parameters = []
                    }

                let second =
                    { first with
                        MessageKey = "host.DUPLICATE"
                    }

                Expect.hasLength
                    (DiagnosticCode.create [ first ] |> Map.toList)
                    1
                    "A declared diagnostic is retained"

                Expect.throws
                    (fun () -> DiagnosticCode.create [ first; second ] |> ignore)
                    "The actual constructor rejects an ambiguous identity")
            testCase "registry retains direct keys and declared parameter contracts" (fun () ->
                Expect.equal
                    (DiagnosticCode.key "INTERNAL")
                    "host.INTERNAL"
                    "Canonical host code is unambiguous"

                Expect.equal
                    (DiagnosticCodecs.definitions |> List.map _.Id |> Set.ofList)
                    (Inventory.tokens "refusalCodes" |> Set.ofList)
                    "Every refusal is independently inventoried"

                let tooLong = DiagnosticCode.all["TOO_LONG"]
                Expect.equal tooLong.MessageKey "diagnostic.TOO_LONG" "An issue resolves directly"

                Expect.equal
                    (tooLong.Parameters |> List.map _.Name)
                    [ "maximumCharacters" ]
                    "The declared argument survives aggregation"

                Expect.throws
                    (fun () -> DiagnosticCode.key "UNDECLARED" |> ignore)
                    "No guessed message key is admitted")
            testCase "catalog subjects retain closed wire spellings" (fun () ->
                let codec = VocabularyCodec.create CatalogSubject.vocabulary

                for value, token in
                    [
                        CatalogSubject.Case, "CASE"
                        CatalogSubject.Authority, "AUTHORITY"
                        CatalogSubject.Operation, "OPERATION"
                        CatalogSubject.Context, "CONTEXT"
                    ] do
                    let bytes = System.Text.Encoding.UTF8.GetBytes("\"" + token + "\"")

                    Expect.equal
                        (ClaimCore.Json.Codec.decode 100 codec bytes)
                        (Ok value)
                        "Each supported spelling is admitted"

                Expect.isError
                    (ClaimCore.Json.Codec.decode 100 codec "\"UNKNOWN\""B)
                    "A free-form catalog subject is refused")
        ]
