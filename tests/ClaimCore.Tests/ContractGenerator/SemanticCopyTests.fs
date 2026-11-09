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
            testCase "payment limits explain entries and retained decision currency" (fun () ->
                Expect.stringContains
                    (text "en" "diagnostic.PAYMENT_EXCEEDS_PAYABLE")
                    "cannot exceed"
                    "The amount limit is explicit"

                Expect.stringContains
                    (text "en" "diagnostic.DECISION_HAS_PAYMENTS")
                    "cannot be withdrawn or cleared"
                    "Retained payments prevent withdrawal and clearing"

                for key in [ "diagnostic.DECISION_HAS_PAYMENTS"; "diagnostic.PAYMENT_COUNT_RANGE" ] do
                    Expect.stringContains
                        (text "en" key)
                        "only if"
                        "Removal is conditional error correction"

                    Expect.stringContains
                        (text "en" key)
                        "recorded in error"
                        "Accurate payments must not be discarded")
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
                            "notification and reminder dates sort oldest first"
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

                    for key in
                        [
                            "label.businessDate"
                            "cli.text.workBusinessDate"
                            "ui.shell.businessDate"
                        ] do
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
