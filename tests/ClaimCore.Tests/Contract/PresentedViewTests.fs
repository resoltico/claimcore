module ClaimCore.Tests.PresentedViewTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Cli
open ClaimCore.Json

let private decode text =
    Codec.decode 10000 PresentationCodecs.view (Encoding.UTF8.GetBytes(text: string))

[<Tests>]
let tests =
    testList
        "wire presentation screens"
        [
            testCase "header progress and assignment codes have closed wire vocabularies" (fun () ->
                let literal =
                    """{"regions":[{"role":"IDENTITY","content":{"kind":"ENTRIES","entries":[{"key":"progress","labelId":"input.progress.label","prominence":"PRIMARY","value":{"kind":"CODE","vocabulary":"progress","token":"UNDECIDED"}},{"key":"assignment","labelId":"input.assignment.label","prominence":"SECONDARY","value":{"kind":"CODE","vocabulary":"assignment","token":"MINE"}}]}}]}"""

                let value =
                    decode literal |> Result.defaultWith (fun error -> failtestf "%A" error)

                Expect.equal
                    (PresentationCodecs.view.Write value
                     |> CanonicalWriter.bytes
                     |> Encoding.UTF8.GetString
                     |> decode)
                    (Ok value)
                    "Both namespaces encode and decode"

                Expect.isError
                    (decode (literal.Replace("UNDECIDED", "UNKNOWN")))
                    "An unknown progress token remains rejected")
            testCase
                "entries admit server-defined keys and roles without a business-field lookup"
                (fun () ->
                    let literal =
                        """{"regions":[{"role":"FACTS","titleId":"subject.CASE.label","content":{"kind":"ENTRIES","entries":[{"key":"future-field","labelId":"field.caseReference.label","prominence":"PRIMARY","value":{"kind":"REFERENCE","value":"CASE-1"}}]}}]}"""

                    let expected =
                        Screen.create
                            [
                                {
                                    Link = None
                                    Actions = []
                                    Role = RegionRole.Facts
                                    TitleId = Some "subject.CASE.label"
                                    EmptyId = None
                                    Count = None
                                    Content =
                                        RegionContent.Entries
                                            [
                                                {
                                                    Recorded = None
                                                    Key = "future-field"
                                                    LabelId = "field.caseReference.label"
                                                    Prominence = Prominence.Primary
                                                    Value =
                                                        PresentedValue.Reference(
                                                            CaseReference.parse "CASE-1"
                                                            |> Result.defaultWith (
                                                                string >> failtest
                                                            )
                                                        )
                                                }
                                            ]
                                }
                            ]

                    Expect.equal
                        (decode literal)
                        (Ok expected)
                        "The literal owns the expected screen"

                    Expect.stringContains
                        (ViewText.render (RenderingContext.forLanguage "en") expected
                         |> String.concat "\n")
                        "CASE-1"
                        "CLI consumes the supplied value"

                    Expect.isError
                        (decode """{"kind":"RECORD","sections":[]}""")
                        "The retired view envelope is refused")
            testCase
                "table rows have stable unique keys complete values and optional details"
                (fun () ->
                    let literal =
                        """{"regions":[{"role":"RESULTS","content":{"kind":"TABLE","columns":[{"key":"future-count","labelId":"label.sequence","prominence":"PRIMARY","isMoney":false}],"rows":[{"key":"entity-7","values":[{"kind":"COUNT","value":7}]}]}}]}"""

                    let column =
                        {
                            Recorded = false
                            Column.Key = "future-count"
                            LabelId = "label.sequence"
                            Prominence = Prominence.Primary
                            IsMoney = false
                        }

                    let table =
                        {
                            Meter = None
                            RegionTable.Columns = [ column ]
                            Rows =
                                [
                                    {
                                        Link = None
                                        Actions = []
                                        Key = "entity-7"
                                        Transfers = []
                                        Recommended = None
                                        Prominence = None
                                        Values = [ PresentedValue.Count 7L ]
                                        Detail = None
                                    }
                                ]
                            Groups = []
                            Sections = []
                        }

                    let expected =
                        Screen.create
                            [
                                {
                                    Link = None
                                    Actions = []
                                    Role = RegionRole.Results
                                    TitleId = None
                                    EmptyId = None
                                    Count = None
                                    Content = RegionContent.Table table
                                }
                            ]

                    Expect.equal
                        (decode literal)
                        (Ok expected)
                        "Stable identity and supplied values survive"

                    let empty =
                        Screen.mapContent
                            (fun _ _ -> RegionContent.Table { table with Rows = [] })
                            expected

                    Expect.equal
                        (PresentationCodecs.view.Write empty |> PresentationCodecs.view.Read "$")
                        (Ok empty)
                        "An empty page retains columns"

                    Expect.isError
                        (decode (literal.Replace("[{\"kind\":\"COUNT\",\"value\":7}]", "[]")))
                        "Incomplete rows refuse before rendering"

                    let duplicate =
                        literal.Replace(
                            "\"rows\":[",
                            "\"rows\":[{\"key\":\"entity-7\",\"values\":[{\"kind\":\"COUNT\",\"value\":1}]},"
                        )

                    Expect.isError (decode duplicate) "A repeated entity key cannot own two rows")
            testCase "roles are closed and only FACTS can repeat" (fun () ->
                let region = """{"role":"WORK","content":{"kind":"ENTRIES","entries":[]}}"""

                let document values =
                    "{\"regions\":[" + String.concat "," values + "]}"

                for token, role in [ "RESULTS", RegionRole.Results; "NOTICE", RegionRole.Notice ] do
                    let singleton = region.Replace("WORK", token)

                    Expect.isError
                        (decode (document [ singleton; singleton ]))
                        "Duplicate roles refuse"

                    let placed = ScreenEntries.region role None (RegionContent.Entries [])

                    Expect.throwsT<System.ArgumentException>
                        (fun () -> Screen.create [ placed; placed ] |> ignore)
                        "Duplicate placements cannot construct a screen"

                let facts = region.Replace("WORK", "FACTS")
                let singleton = region.Replace("WORK", "RESULTS")

                let view =
                    decode (document [ facts; singleton; facts; region ])
                    |> Result.defaultWith (sprintf "%A" >> failtest)

                Expect.equal
                    (view.Regions |> List.map _.Role)
                    [ RegionRole.Facts; RegionRole.Results; RegionRole.Facts; RegionRole.Work ]
                    "The decoder keeps interspersed Facts in authored order"

                Expect.isError
                    (decode ("{\"regions\":[" + region.Replace("WORK", "UNDECLARED") + "]}"))
                    "Unknown roles refuse")
            testCase
                "DETAILS is absent from ordinary CLI output and explicit when requested"
                (fun () ->
                    let screen =
                        Screen.create
                            [
                                {
                                    Link = None
                                    Actions = []
                                    Role = RegionRole.Identity
                                    TitleId = None
                                    EmptyId = None
                                    Count = None
                                    Content =
                                        RegionContent.Entries
                                            [
                                                {
                                                    Recorded = None
                                                    Key = "case"
                                                    LabelId = "field.caseReference.label"
                                                    Prominence = Prominence.Primary
                                                    Value = PresentedValue.Text "CASE-1"
                                                }
                                            ]
                                }
                                {
                                    Link = None
                                    Actions = []
                                    Role = RegionRole.Details
                                    TitleId = Some "label.technicalDetails"
                                    EmptyId = None
                                    Count = None
                                    Content =
                                        RegionContent.Entries
                                            [
                                                {
                                                    Recorded = None
                                                    Key = "version"
                                                    LabelId = "label.revisionLabel"
                                                    Prominence = Prominence.Detail
                                                    Value =
                                                        PresentedValue.Identifier
                                                            "TECHNICAL-VERSION"
                                                }
                                            ]
                                }
                            ]

                    Expect.isFalse
                        ((ViewText.render (RenderingContext.forLanguage "en") screen
                          |> String.concat "\n")
                            .Contains
                            "TECHNICAL-VERSION")
                        "Default text stays human"

                    Expect.stringContains
                        (ViewText.renderSelected true (RenderingContext.forLanguage "en") screen
                         |> String.concat "\n")
                        "TECHNICAL-VERSION"
                        "Details are opt-in")
        ]
