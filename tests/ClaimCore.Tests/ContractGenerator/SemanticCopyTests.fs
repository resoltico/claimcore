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
                "every language preserves both settlement branches, all payment choices and unresolved recovery"
                (fun () ->
                    // Independently authored witnesses name the omitted branches/choices, not renderer output.
                    let witnesses =
                        [
                            "en",
                            "already accepted for your account",
                            "Otherwise",
                            "reaffirm, replace or clear",
                            "may remain unresolved",
                            "permanent settlement when available"
                            "lv",
                            "jūsu kontam jau ir pieņemta",
                            "Pretējā gadījumā",
                            "apstipriniet, aizstājiet vai noņemiet",
                            "var palikt nenoskaidrots",
                            "pieprasīt neatgriezenisku atrisināšanu"
                            "ar",
                            "قُبلت لحسابك",
                            "وإلا",
                            "أكّد سجل الدفع مجددًا أو استبدله أو امسحه",
                            "قد تبقى غير محسومة",
                            "لطلب تسوية نهائية عن قصد"
                            "ko",
                            "이미 이 계정에 수락",
                            "그렇지 않으면",
                            "재확인, 교체 또는 삭제",
                            "계속 미확정 상태일 수",
                            "영구 확정을 명시적으로 요청"
                            "ja",
                            "既にこのアカウントで受理",
                            "それ以外の場合",
                            "再確認、置換、削除",
                            "未確定のままの場合",
                            "明示的に最終確定を依頼"
                            "es",
                            "ya se aceptó para tu cuenta",
                            "En caso contrario",
                            "confirma de nuevo, sustituye o elimina",
                            "puede seguir sin resolverse",
                            "solicitar deliberadamente una resolución permanente"
                            "de",
                            "für Ihr Konto bereits angenommen",
                            "Andernfalls",
                            "erneut bestätigen, ersetzen oder entfernen",
                            "kann ungeklärt bleiben",
                            "bewusst eine dauerhafte Klärung anfordern"
                            "zh-Hans", "已针对此账户被接受", "否则", "重申、替换或清除", "可能仍未确定", "明确请求永久确定操作结果"
                            "zh-Hant", "已針對此帳戶被接受", "否則", "重申、替換或清除", "可能仍未確定", "明確請求永久確定操作結果"
                        ]

                    Expect.equal
                        (witnesses |> List.map (fun (l, _, _, _, _, _) -> l) |> Set.ofList)
                        (Set.ofList LocalizationPolicy.languages)
                        "Every supported language has independent meaning witnesses"

                    for language, accepted, otherwise, payment, unresolved, settlement in witnesses do
                        let consequence = text language "endpoint.operation.resolve.consequence"

                        Expect.stringContains
                            consequence
                            accepted
                            "Existing own acceptance is retained"

                        Expect.stringContains
                            consequence
                            otherwise
                            "Nonacceptance is a distinct conditional branch"

                        Expect.stringContains
                            (text language "diagnostic.PAYMENT_ACKNOWLEDGEMENT_REQUIRED")
                            payment
                            "Replacement is offered alongside reaffirm/clear"

                        let retry = text language "ui.retryExactDescription"

                        Expect.stringContains
                            retry
                            unresolved
                            "Passive status does not promise finality"

                        Expect.stringContains
                            retry
                            settlement
                            "Identifier-only recovery can offer deliberate settlement")
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
            testCase
                "active open work is distinct from attention and uses the current business date"
                (fun () ->
                    Expect.equal
                        (text "de" "label.actionableCases")
                        "Aktive offene Fälle"
                        "Not an attention count"

                    Expect.equal
                        (text "es" "label.actionableCases")
                        "Expedientes activos y abiertos"
                        "Not an attention count"

                    Expect.stringContains
                        (text "en" "endpoint.work.overview.meaning")
                        "current business date"
                        "Not the installation setup date")
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
