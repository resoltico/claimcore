module ClaimCore.Tests.AuthorityHistoryTests

open System
open System.IO
open System.Text
open System.Text.Json
open Microsoft.VisualBasic.FileIO
open Expecto
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.TestSupport

let private accepted result =
    result |> Result.defaultWith (fun error -> failtestf "%A" error)

let private optionalActorFilter () =
    let actor = "10000000-0000-4000-8000-000000000001"

    let prepared =
        Grammar.prepare
            (fun () -> actor)
            [
                "authority"
                "history"
                "--actor-id"
                actor
                "--limit"
                "1"
                "--format"
                "csv"
            ]
        |> accepted

    Expect.equal prepared.Format VerbFormat.Csv "Explicit safe CSV export"
    let frame = Frames.admit prepared.Bytes |> accepted
    Expect.equal (Frames.endpoint frame).Id Api.authorityHistory.Id "Derived read endpoint"
    let request = Api.authorityHistory.Input.Read "$" (Frames.input frame) |> accepted
    Expect.equal request.TargetActorId (Some(Guid.Parse actor)) "Optional target filter"

    Expect.isError
        (Grammar.prepare (fun () -> actor) [ "authority"; "history"; actor ])
        "An optional filter cannot become an accidental positional operand"

let private csvSubjects () =
    let subject = "=SUM(A1),\"quoted\""
    let principal = PrincipalKey.human "https://issuer.example/" subject |> accepted
    let actor = CaseFixtures.actorId (Guid.Parse "10000000-0000-4000-8000-000000000001")

    let entry =
        {
            AuthorityHistoryEntry.Sequence = Sequence.first
            RecordedAt = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            RecordedBy = Inputs.actorReference actor
            Target = Inputs.actorReference actor
            Facts = AuthorityHistoryFacts.Registered principal
        }

    let csv =
        Array.append AuthorityHistoryCsv.csvHeader (AuthorityHistoryCsv.csvRows [ entry ])

    use source = new StringReader(Encoding.UTF8.GetString csv)
    use reader = new TextFieldParser(source)
    reader.SetDelimiters [| "," |]
    reader.HasFieldsEnclosedInQuotes <- true
    let header = reader.ReadFields() |> nonNull
    let values = reader.ReadFields() |> nonNull
    Expect.equal header[9] "principal.subject" "Machine-readable column identity"
    Expect.equal values[9] ("'" + subject) "Protection precedes independent RFC4180 decoding"

    let page =
        {
            AuthorityHistoryPage.Basis = Inputs.workBasis
            Entries = [ entry ]
            NextCursor = None
        }

    let text =
        ViewText.render "en" (Descriptor.tableView Descriptor.authorityHistoryColumns page.Entries)
        |> String.concat "\n"

    Expect.isFalse
        (text.Contains "{\"")
        "The authority table renders human text; exact identity is retained in CSV and JSON"

    use json =
        JsonDocument.Parse(
            Codec.encode
                Api.authorityHistory.Output
                (Outcome.Succeeded(
                    Presented.create
                        (fun (page: AuthorityHistoryPage) ->
                            Descriptor.tableView Descriptor.authorityHistoryColumns page.Entries)
                        page
                ))
        )

    let data =
        json.RootElement.GetProperty("outcome").GetProperty("data").GetProperty("entries")

    let actual =
        data[0].GetProperty("act").GetProperty("principal").GetProperty("subject").GetString()

    Expect.equal actual subject "Structured output stays exact"

[<Tests>]
let tests =
    testList
        "authority history CLI"
        [
            testCase "optional actor filtering is named and CSV is admitted" optionalActorFilter
            testCase
                "CSV protects principal subjects while JSON and text retain authored values"
                csvSubjects
        ]
