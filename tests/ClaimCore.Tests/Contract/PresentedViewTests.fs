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
            testCase
                "entries admit server-defined keys and roles without a business-field lookup"
                (fun () ->
                    let literal =
                        """{"regions":[{"role":"FACTS","titleId":"subject.CASE.label","content":{"kind":"ENTRIES","entries":[{"key":"future-field","labelId":"field.caseReference.label","prominence":"PRIMARY","value":{"kind":"REFERENCE","value":"CASE-1"}}]}}]}"""

                    let expected =
                        {
                            Screen.Regions =
                                [
                                    {
                                        Role = RegionRole.Facts
                                        TitleId = Some "subject.CASE.label"
                                        Content =
                                            RegionContent.Entries
                                                [
                                                    {
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
                        }

                    Expect.equal
                        (decode literal)
                        (Ok expected)
                        "The literal owns the expected screen"

                    Expect.stringContains
                        (ViewText.render "en" expected |> String.concat "\n")
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
                            Column.Key = "future-count"
                            LabelId = "label.sequence"
                            Prominence = Prominence.Primary
                            IsMoney = false
                        }

                    let table =
                        {
                            RegionTable.Columns = [ column ]
                            Rows =
                                [
                                    {
                                        Key = "entity-7"
                                        Values = [ PresentedValue.Count 7L ]
                                        Detail = None
                                    }
                                ]
                            Groups = []
                            Sections = []
                        }

                    let expected =
                        {
                            Screen.Regions =
                                [
                                    {
                                        Role = RegionRole.Results
                                        TitleId = None
                                        Content = RegionContent.Table table
                                    }
                                ]
                        }

                    Expect.equal
                        (decode literal)
                        (Ok expected)
                        "Stable identity and supplied values survive"

                    let empty =
                        { expected with
                            Regions =
                                [
                                    { expected.Regions.Head with
                                        Content = RegionContent.Table { table with Rows = [] }
                                    }
                                ]
                        }

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

                Expect.isError
                    (decode ("{\"regions\":[" + region + "," + region + "]}"))
                    "Repeated work refuses"

                Expect.isOk
                    (decode (
                        "{\"regions\":["
                        + region.Replace("WORK", "FACTS")
                        + ","
                        + region.Replace("WORK", "FACTS")
                        + "]}"
                    ))
                    "Separate fact groups are allowed"

                Expect.isError
                    (decode ("{\"regions\":[" + region.Replace("WORK", "UNDECLARED") + "]}"))
                    "Unknown roles refuse")
            testCase
                "DETAILS is absent from ordinary CLI output and explicit when requested"
                (fun () ->
                    let screen =
                        {
                            Screen.Regions =
                                [
                                    {
                                        Role = RegionRole.Identity
                                        TitleId = None
                                        Content =
                                            RegionContent.Entries
                                                [
                                                    {
                                                        Key = "case"
                                                        LabelId = "field.caseReference.label"
                                                        Prominence = Prominence.Primary
                                                        Value = PresentedValue.Text "CASE-1"
                                                    }
                                                ]
                                    }
                                    {
                                        Role = RegionRole.Details
                                        TitleId = Some "label.recordDetails"
                                        Content =
                                            RegionContent.Entries
                                                [
                                                    {
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
                        }

                    Expect.isFalse
                        ((ViewText.render "en" screen |> String.concat "\n").Contains
                            "TECHNICAL-VERSION")
                        "Default text stays human"

                    Expect.stringContains
                        (ViewText.renderSelected true "en" screen |> String.concat "\n")
                        "TECHNICAL-VERSION"
                        "Details are opt-in")
        ]
