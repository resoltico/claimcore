module ClaimCore.Tests.PaymentCursorTests

open System
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Json

let private accepted value =
    Result.defaultWith (fun error -> failtestf "%A" error) value

let private installation = Guid.Parse "40000000-0000-4000-8000-000000000001"

let private parent =
    CaseId.create (Guid.Parse "50000000-0000-4000-8000-000000000001") |> Option.get

let private position sequence index =
    EffectPosition.create (Sequence.create sequence |> Option.get) index
    |> Option.get

[<Tests>]
let tests =
    testList
        "payment paging contract"
        [
            testCase "positions distinguish effects within one operation" (fun () ->
                Expect.isTrue
                    (position 5L 2 > position 5L 1)
                    "Effect ordinal participates in ordering"

                Expect.isTrue (position 6L 0 > position 5L 2) "Operation sequence leads ordering"
                Expect.isNone (EffectPosition.create Sequence.zero 0) "No accepted genesis effect"
                Expect.isNone (EffectPosition.create Sequence.first -1) "No negative ordinal")
            testCase "cursor is bound to installation case and declared selection" (fun () ->
                let expected = position 5L 2
                let cursor = Cursors.encodePayments installation parent expected

                Expect.equal
                    (Cursors.decodePayments installation parent cursor)
                    (Ok expected)
                    "Positive scoped control"

                Expect.isError
                    (Cursors.decodePayments
                        (Guid.Parse "40000000-0000-4000-8000-000000000002")
                        parent
                        cursor)
                    "Foreign installation"

                Expect.isError
                    (Cursors.decodePayments
                        installation
                        (CaseId.create (Guid.Parse "50000000-0000-4000-8000-000000000002")
                         |> Option.get)
                        cursor)
                    "Foreign case"

                let raw =
                    Convert.FromBase64String(
                        cursor.Replace('-', '+').Replace('_', '/')
                        + String('=', (4 - cursor.Length % 4) % 4)
                    )
                    |> Text.Encoding.UTF8.GetString

                let tampered =
                    raw.Replace("ACTIVE", "REMOVED")
                    |> Text.Encoding.UTF8.GetBytes
                    |> Convert.ToBase64String
                    |> fun value -> value.TrimEnd('=').Replace('+', '-').Replace('/', '_')

                Expect.isError
                    (Cursors.decodePayments installation parent tampered)
                    "Same framing with another selection is refused")
            testCase "omitted limit means twenty and explicit read maximum is fifty" (fun () ->
                let request =
                    RequestCodecs.casePaymentsRequest.Read
                        "$"
                        (JsonValue.Object
                            [
                                "target",
                                JsonValue.Object
                                    [
                                        "kind", JsonValue.String "ID"
                                        "caseId", JsonValue.String(CaseId.format parent)
                                    ]
                            ])
                    |> accepted

                Expect.equal request.Limit 20 "Independent normative default"

                for limit, valid in [ 0L, false; 1L, true; 50L, true; 51L, false ] do
                    let raw =
                        JsonValue.Object
                            [
                                "target",
                                JsonValue.Object
                                    [
                                        "kind", JsonValue.String "ID"
                                        "caseId", JsonValue.String(CaseId.format parent)
                                    ]
                                "limit", JsonValue.Integer limit
                            ]

                    Expect.equal
                        (RequestCodecs.casePaymentsRequest.Validate "$" raw |> Result.isOk)
                        valid
                        "Read limits are not lifetime payment limits")
            testCase "authored HUMAN payment rows require matching declared handles" (fun () ->
                let literal =
                    """{"representation":"HUMAN","outcome":{"kind":"SUCCEEDED","document":{"screen":{"regions":[{"role":"PAYMENTS","content":{"kind":"TABLE","columns":[{"key":"saved","labelId":"label.details","prominence":"PRIMARY","isMoney":false}],"rows":[{"key":"019aa6a0-0000-7000-8000-000000000002","values":[{"kind":"TEXT","value":"Independent core caption Ω"}]}]}}]},"context":{"kind":"PAYMENT_PAGE","caseId":"019aa6a0-0000-7000-8000-000000000001","revision":3,"nextCursor":null,"payments":[{"paymentId":"019aa6a0-0000-7000-8000-000000000002","canRemove":true}]},"basis":{"kind":"SNAPSHOT","observation":{"observedAt":"2026-11-09T12:00:00.000000Z","businessDate":"2026-11-09","timeZone":"Etc/UTC"}},"disclosure":"CURRENT"}}}"""

                let raw (value: string) =
                    System.Text.Encoding.UTF8.GetBytes value |> StrictJson.read 10000 |> accepted

                Expect.isOk
                    (Api.casePayments.Output.Validate "$" (raw literal))
                    "Positive independent document"

                let orphan =
                    literal.Replace(
                        "\"paymentId\":\"019aa6a0-0000-7000-8000-000000000002\"",
                        "\"paymentId\":\"019aa6a0-0000-7000-8000-000000000009\""
                    )

                Expect.isError
                    (Api.casePayments.Output.Validate "$" (raw orphan))
                    "Saved row cannot refer to an undeclared handle"

                Expect.isError
                    (Api.casePayments.Output.Validate
                        "$"
                        (raw (literal.Replace("CURRENT", "ERASED"))))
                    "Erasure cannot retain private row handles")
            testCase
                "native raw financial shape agrees without moving equations to transport"
                (fun () ->
                    let literal =
                        """{"representation":"DATA","outcome":{"kind":"SUCCEEDED","data":{"basis":{"observedAt":"2026-11-09T12:00:00.000000Z","businessDate":"2026-11-09","timeZone":"Etc/UTC"},"caseId":"019aa6a0-0000-7000-8000-000000000001","caseReference":"FIN-1","revision":3,"state":{"kind":"ACTIVE","finance":{"decision":{"paymentDecisionDate":"2026-01-10","payableAmount":"10","payableCurrency":"EUR"},"paidAmount":"99","paymentCount":1,"firstPaymentDate":"2026-01-11","lastPaymentDate":"2026-01-11"}},"items":[],"nextCursor":null}}}"""

                    let raw =
                        System.Text.Encoding.UTF8.GetBytes literal
                        |> StrictJson.read 10000
                        |> accepted

                    Expect.isOk (Api.casePayments.Output.Validate "$" raw) "Structurally valid DTO"

                    Expect.isError
                        (Api.casePayments.Output.Read "$" raw)
                        "Core financial constructor independently refuses overpayment")

        ]
