module ClaimCore.Tests.PresentedViewTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Cli

let private decode text =
    Codec.decode 10000 PresentationCodecs.view (Encoding.UTF8.GetBytes(text: string))

[<Tests>]
let tests =
    testList
        "wire presentation views"
        [
            testCase "record admits and renders a server-defined entry key" (fun () ->
                let literal =
                    """{"kind":"RECORD","sections":[{"titleId":"subject.CASE.label","entries":[{"key":"future-field","labelId":"field.caseReference.label","prominence":"PRIMARY","value":{"kind":"REFERENCE","value":"CASE-1"}}]}]}"""

                let expected =
                    View.Record
                        [
                            {
                                TitleId = "subject.CASE.label"
                                Entries =
                                    [
                                        {
                                            Key = "future-field"
                                            LabelId = "field.caseReference.label"
                                            Prominence = Prominence.Primary
                                            Value =
                                                PresentedValue.Reference(
                                                    CaseReference.parse "CASE-1"
                                                    |> Result.defaultWith (string >> failtest)
                                                )
                                        }
                                    ]
                            }
                        ]

                Expect.equal
                    (decode literal)
                    (Ok expected)
                    "The literal contract owns the expected record."

                Expect.stringContains
                    (ViewText.render "en" expected |> String.concat "\n")
                    "CASE-1"
                    "CLI consumes the supplied value without a business-field lookup.")
            testCase "payment reaffirmation intent survives the wire" (fun () ->
                let literal =
                    """{"kind":"RECORD","sections":[{"titleId":"group.payment.label","entries":[{"key":"intent","labelId":"label.action","prominence":"PRIMARY","value":{"kind":"CODE","vocabulary":"correction","token":"REAFFIRM"}}]}]}"""

                Expect.isOk
                    (decode literal)
                    "The host must be able to deliver authored payment intent.")
            testCase "table retains empty-page metadata and exact supplied values" (fun () ->
                let literal =
                    """{"kind":"TABLE","columns":[{"key":"future-count","labelId":"label.sequence","prominence":"PRIMARY","isMoney":false}],"rows":[[{"kind":"COUNT","value":7}]],"details":[null],"sections":[]}"""

                let column =
                    {
                        Column.Key = "future-count"
                        LabelId = "label.sequence"
                        Prominence = Prominence.Primary
                        IsMoney = false
                    }

                Expect.equal
                    (decode literal)
                    (Ok(View.Table([ column ], [ [ PresentedValue.Count 7L ] ], [ None ], [])))
                    "Row order and values are supplied."

                let empty = View.Table([ column ], [], [], [])

                Expect.equal
                    (decode (Codec.encode PresentationCodecs.view empty |> Encoding.UTF8.GetString))
                    (Ok empty)
                    "An empty page still owns its columns."

                Expect.isError
                    (decode (literal.Replace("\"details\":[null]", "\"details\":[]")))
                    "Every supplied row has exactly one detail slot."

                Expect.isError
                    (decode (literal.Replace("[[{\"kind\":\"COUNT\",\"value\":7}]]", "[[]]")))
                    "An incomplete row cannot crash a terminal renderer.")
        ]
