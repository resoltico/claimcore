module ClaimCore.Tests.SemanticCopyTests

open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.ContractGeneration

let private catalogs =
    AuthoredMessages.all () |> Map.map (fun _ entries -> Map.ofList entries)

let private text language key = catalogs[language][key]

[<Tests>]
let tests =
    testList
        "semantic catalog guidance"
        [
            testCase
                "payment guidance describes entry currency and the exact total limit"
                (fun () ->
                    Expect.equal
                        (text "en" "field.paidCurrency.hint")
                        "Use the currency of this entry."
                        "Payment entries carry their own currency"

                    Expect.stringContains
                        (text "en" "diagnostic.PAYMENT_TOTAL_RANGE")
                        "outside the exact range"
                        "The mathematical range is an explicit input refusal"

                    Expect.stringContains
                        (text "en" "task.case.withdrawDecision.meaning")
                        "Existing payment entries"
                        "Withdrawal preserves accepted entries")
            testCase
                "payment count saturation is technical rather than a business capacity rule"
                (fun () ->
                    Expect.isFalse
                        (Map.containsKey "diagnostic.PAYMENT_COUNT_RANGE" catalogs["en"])
                        "The retired total50 diagnostic is absent"

                    Expect.stringContains
                        (text "en" "diagnostic.PAYMENT_COUNT_LIMIT")
                        "exact-integer"
                        "Count saturation has an explicit technical bound")
            testCase "English explains final lookup and automatic unconfirmed checks" (fun () ->
                Expect.stringContains
                    (text "en" "endpoint.operation.get.meaning")
                    "whether a change was recorded"
                    "Acceptance is observable"

                Expect.stringContains
                    (text "en" "endpoint.operation.get.meaning")
                    "{window}"
                    "Absence is bounded by expiry"

                Expect.stringContains
                    (text "en" "ui.recoveryDescription")
                    "checks them for you"
                    "Restart recovery checks identity only")

            testCase
                "English ordering names opposite directions and absent-date position"
                (fun () ->
                    let rule = text "en" "input.order.rule"

                    for witness in
                        [
                            "Fact changes sort newest first"
                            "Received and reminder dates sort oldest first"
                            "missing dates last"
                            "Reference breaks ties"
                        ] do
                        Expect.stringContains rule witness "Matches the declared SQL ordering")
            testCase "combined view counts describe both sets and the business day" (fun () ->
                let meaning = text "en" "endpoint.work.views.meaning"
                Expect.stringContains meaning "cases and reminders" "Both count sets are described"
                Expect.stringContains meaning "today" "Counts refer to the observed business day")
            testCase
                "recorded opening milestones and Korean reference dates are not current active states or working-day rules"
                (fun () ->
                    Expect.equal (text "ko" "caseStage.OPENED.label") "등록됨" "Historical opening"
                    Expect.equal (text "ja" "caseStage.OPENED.label") "登録済み" "Historical opening"

                    Expect.equal
                        (text "ko" "status.OPENED.label")
                        "진행 중"
                        "Current status keeps its distinct meaning"

                    Expect.equal
                        (text "ja" "status.OPENED.label")
                        "対応中"
                        "Current status keeps its distinct meaning"

                    for key in [ "label.businessDate"; "cli.text.workBusinessDate" ] do
                        Expect.equal
                            (text "ko" key)
                            "업무 기준일"
                            "Calendar reference date, including weekends")
            testCase
                "Latvian and Arabic faults preserve operator repair instead of caller idempotency blame"
                (fun () ->
                    for language, inconsistent, preserve, verify, retry in
                        [
                            "lv",
                            "Saglabātās operācijas saturā ir neatbilstība",
                            "atbilstošo programmas versiju",
                            "administratoram veikt pārbaudi",
                            "Mēģiniet vēlreiz"
                            "ar",
                            "محتوى العملية المخزّن غير متسق",
                            "إصدار البرنامج المطابق",
                            "المسؤول إجراء التحقق",
                            "حاول مجددًا"
                        ] do
                        for key, witness in
                            [
                                "fault.CORE_OPERATION_CONTENT_CONFLICT", inconsistent
                                "fault.CORE_SCHEMA_MISMATCH", preserve
                                "fault.CORE_STORE_INTEGRITY_ERROR", verify
                                "fault.CORE_STORE_UNAVAILABLE", retry
                            ] do
                            Expect.stringContains
                                (text language key)
                                witness
                                "The actual fault retains its distinct operational instruction")
        ]
