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
            testCase "payment correction preserves all three choices in every language" (fun () ->
                let witnesses =
                    [
                        "en", "reaffirm, replace or clear"
                        "lv", "apstipriniet, aizstājiet vai noņemiet"
                        "ar", "أكّد سجل الدفع مجددًا أو استبدله أو امسحه"
                        "ko", "재확인, 교체 또는 삭제"
                        "ja", "再確認、置換、削除"
                        "es", "confirma de nuevo, sustituye o elimina"
                        "de", "erneut bestätigen, ersetzen oder entfernen"
                        "zh-Hans", "重申、替换或清除"
                        "zh-Hant", "重申、替換或清除"
                    ]

                for language, payment in witnesses do
                    Expect.stringContains
                        (text language "diagnostic.PAYMENT_ACKNOWLEDGEMENT_REQUIRED")
                        payment
                        "The independent payment choices remain explicit")
            testCase "English explains final lookup and automatic unconfirmed checks" (fun () ->
                Expect.stringContains
                    (text "en" "endpoint.operation.get.meaning")
                    "accepted receipt"
                    "Acceptance is observable"

                Expect.stringContains
                    (text "en" "endpoint.operation.get.meaning")
                    "lifetime ends"
                    "Absence is bounded by expiry"

                Expect.stringContains
                    (text "en" "ui.retryExactDescription")
                    "automatically"
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
            testCase "combined view counts name their shared snapshot and date basis" (fun () ->
                let meaning = text "en" "endpoint.work.views.meaning"
                Expect.stringContains meaning "same snapshot" "Both count sets share one snapshot"
                Expect.stringContains meaning "date basis" "The date belongs to that snapshot")
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
                            "Saglabājiet šo instalāciju",
                            "Apturiet darbu un palaidiet verify",
                            "Mēģiniet vēlreiz"
                            "ar",
                            "محتوى العملية المخزّن غير متسق",
                            "حافظ على هذا التثبيت",
                            "توقف وشغّل verify",
                            "أعد المحاولة"
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
