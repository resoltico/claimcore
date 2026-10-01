module ClaimCore.DocsTests.ContractTokenTests

open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private declared (ids: string list) =
    ids
    |> List.map (fun id ->
        let anchor = id.ToLowerInvariant()
        $"<a id=\"{anchor}\"></a>\n### {id} — Synthetic contract\n\nMeaning.\n")
    |> String.concat "\n"
    |> markdown "docs/domain.md"
    |> fun document -> Contracts.declarations [ document ] |> requireOk

let private verify (inventory: string) (ids: string list) =
    use repository = new TempRepository()
    repository.Write("tests/inventory/Example.Tests.txt", inventory) |> ignore
    ContractTokens.verify repository.Root (declared ids)

let tests =
    testList
        "contract tokens"
        [
            testCase "accepts every contract named by a test and no unknown token"
            <| fun _ ->
                let inventory =
                    "[CC-DOM-001] keeps the record exact\n[CC-DOM-002] rejects overflow\nunrelated test\n"

                verify inventory [ "CC-DOM-001"; "CC-DOM-002" ] |> requireOk

            testCase "refuses a contract that no test names"
            <| fun _ ->
                let errors =
                    verify "[CC-DOM-001] keeps the record exact\n" [ "CC-DOM-001"; "CC-DOM-002" ]
                    |> requireError

                Expect.isTrue
                    (errors
                     |> List.exists (fun (error: Diagnostic) ->
                         error.Message.Contains("CC-DOM-002")))
                    "The untested contract is named"

            testCase "refuses a token that names no declared contract"
            <| fun _ ->
                let errors =
                    verify "[CC-DOM-001] ok\n[CC-GHOST-007] misspelled\n" [ "CC-DOM-001" ]
                    |> requireError

                Expect.isTrue
                    (errors |> List.exists (fun error -> error.Message.Contains("CC-GHOST-007")))
                    "The unknown token is named"

            testCase "refuses a missing inventory directory"
            <| fun _ ->
                use repository = new TempRepository()

                ContractTokens.verify repository.Root (declared [ "CC-DOM-001" ])
                |> requireError
                |> ignore
        ]
