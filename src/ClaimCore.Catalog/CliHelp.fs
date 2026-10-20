namespace ClaimCore.Catalog

[<RequireQualifiedAccess>]
type CliUsageIssue =
    | Invocation
    | Topic
    | Options
    | Selector

/// Offline task guidance. Technical commands and names are never translated.
module CliHelp =
    let diagnosticTopics = [ InputDiagnostics.protocolId ]

    let topics =
        [
            "overview"
            "help"
            "version"
            "describe"
            "schema"
            "call"
            "session"
            "configuration"
            "delivery"
            "examples"
        ]
        @ diagnosticTopics

    let aliases = [ "--help"; "-h" ]

    let commands = [ "help"; "version"; "describe"; "schema"; "call"; "session" ]

    let private intro language =
        match language with
        | "lv" ->
            "ClaimCore strukturēts HTTPS klients. Palīdzība un apraksti darbojas bez savienojuma; call un session pieprasa autentifikāciju."
        | "ar" ->
            "ClaimCore عميل HTTPS منظّم. المساعدة والوصف يعملان دون اتصال؛ يتطلب call وsession المصادقة."
        | _ ->
            "ClaimCore structured HTTPS client. Help and discovery work offline; call and session require authentication."

    let private guidance language =
        match language with
        | "lv" ->
            "Saglabājiet sākotnējos pieprasījuma baitus, operationId un expectedRevision. Nezināms iznākums nav atteikums. Atkārtojiet tikai precīzu pieprasījumu."
        | "ar" ->
            "احتفظ ببايتات الطلب الأصلية وoperationId وexpectedRevision. النتيجة المجهولة ليست رفضاً. أعد الطلب نفسه فقط."
        | _ ->
            "Preserve original request bytes, operationId and expectedRevision. Unknown outcome is not refusal. Retry only the exact request."

    let private localized language english latvian arabic =
        match language with
        | "lv" -> latvian
        | "ar" -> arabic
        | _ -> english

    let private configuration language =
        [
            localized
                language
                "Required, in failure-check order: service root, issuer, client ID, auth mode. Auth mode: interactive or automation. Automation requires a private secret file; trust roots are optional and local-only. Missing, invalid and private-file refusal are distinct."
                "Obligāti pārbaudes secībā: pakalpojuma sakne, izdevējs, klienta ID, autentifikācijas režīms. Režīms: interactive vai automation. Automation pieprasa privātu noslēpuma failu; uzticamības saknes nav obligātas un ir tikai lokālas. Trūkstoša, nederīga un nepieejama privāta ievade ir atšķirīgi gadījumi."
                "مطلوب بترتيب التحقق: جذر الخدمة، المُصدر، معرّف العميل، وضع المصادقة. الوضع: interactive أو automation. يتطلب automation ملف سر خاصاً؛ جذور الثقة اختيارية ومحلية فقط. الغياب والقيمة غير الصالحة ورفض الملف الخاص أسباب مختلفة."
        ]
        @ [
            InputDiagnostics.configurationId
            + ": setting / issue — "
            + (InputDiagnostics.configurationIssues |> List.map snd |> String.concat " | ")
        ]
        @ (InputDiagnostics.settings |> List.map snd)

    let private delivery language =
        [
            guidance language
            (localized
                language
                "exit 4: unconfirmed client delivery; OUTCOME_UNKNOWN: lost commit confirmation"
                "exit 4: neapstiprināta piegāde; OUTCOME_UNKNOWN: zudis darījuma apstiprinājums"
                "exit 4: تسليم غير مؤكّد؛ OUTCOME_UNKNOWN: فُقد تأكيد الالتزام")
            (localized
                language
                "NOT_STARTED describes this attempt only. Earlier uncertainty survives."
                "NOT_STARTED attiecas tikai uz šo mēģinājumu. Iepriekšēja nenoteiktība saglabājas."
                "يصف NOT_STARTED هذه المحاولة فقط. يبقى عدم اليقين السابق.")
        ]

    let private framing language invocationBytes requestBytes responseBytes =
        [
            (localized
                language
                "call: one UTF-8 JSON document, multiline allowed; close stdin (EOF)."
                "call: viens UTF-8 JSON dokuments; atļautas vairākas rindas; aizveriet stdin (EOF)."
                "call: مستند JSON واحد بترميز UTF-8؛ يسمح بعدة أسطر؛ أغلق stdin (EOF).")
            (localized
                language
                "session: NDJSON, one complete frame per physical line; process-local sign-in reuse, no saved credentials."
                "session: NDJSON, viens pilns ziņojums katrā rindā; pierakstīšanās tiek atkārtoti izmantota tikai procesā, bez saglabātiem akreditācijas datiem."
                "session: NDJSON، إطار كامل في كل سطر؛ تُستخدم المصادقة داخل العملية فقط دون حفظ بيانات الاعتماد.")
            "Invocation bytes <= "
            + string invocationBytes
            + "; request input bytes <= "
            + string requestBytes
            + "; response bytes <= "
            + string responseBytes
            guidance language
            "ClaimCore.Cli schema invocation"
            "ClaimCore.Cli help configuration"
        ]

    let private content language topic invocationBytes requestBytes responseBytes =
        match topic with
        | "overview"
        | "help" ->
            [
                String.concat " | " topics
                String.concat
                    " | "
                    (aliases @ (aliases |> List.map (fun flag -> "<command> " + flag)))
            ]
        | "configuration" -> configuration language
        | "delivery" -> delivery language
        | "call"
        | "session" -> framing language invocationBytes requestBytes responseBytes
        | topic when List.contains topic diagnosticTopics ->
            framing language invocationBytes requestBytes responseBytes
            @ [
                "ClaimCore.Cli schema response"
                "ClaimCore.Cli help call"
                "ClaimCore.Cli help session"
                "ClaimCore.Cli help delivery"
            ]
        | "describe" ->
            [
                "summary | fields [name] | commands [kind] | authority [kind] | endpoints [id] | diagnostics"
                (localized
                    language
                    "JSON is the default. Text describes static capabilities, never current eligibility."
                    "Noklusējums ir JSON. Teksts apraksta statiskas iespējas, nevis pašreizējās tiesības."
                    "JSON هو الافتراضي. يصف النص الإمكانات الثابتة ولا يثبت الأهلية الحالية.")
            ]
        | "schema" ->
            [
                "ClaimCore.Cli schema <name>"
                "ClaimCore.Cli schema <endpoint> request|response"
                "openapi | invocation | response | host-failure"
                (localized language "JSON only." "Tikai JSON." "JSON فقط.")
            ]
        | "version" -> [ "ClaimCore.Cli version [--json]" ]
        | "examples" ->
            [
                "printf '%s\\n' '{\"endpoint\":\"context\",\"input\":{}}' | ClaimCore.Cli call"
                "ClaimCore.Cli describe commands OPEN --format text --language lv"
                "ClaimCore.Cli schema case.execute request"
            ]
        | _ -> []

    let text language topic invocationBytes requestBytes responseBytes =
        let common =
            [
                intro language
                "ClaimCore.Cli help [topic] [--language en|lv|ar]"
                "ClaimCore.Cli describe [selector] [--format json|text]"
            ]

        String.concat
            "\n"
            (common @ content language topic invocationBytes requestBytes responseBytes)
        + "\n"

    let usage language issue =
        let category =
            match issue with
            | CliUsageIssue.Invocation ->
                localized
                    language
                    "Unsupported invocation."
                    "Neatbalstīts izsaukums."
                    "استدعاء غير مدعوم."
            | CliUsageIssue.Topic ->
                localized
                    language
                    "Unknown help topic."
                    "Nezināma palīdzības tēma."
                    "موضوع مساعدة غير معروف."
            | CliUsageIssue.Options ->
                localized
                    language
                    "Unsupported or duplicate options."
                    "Neatbalstītas vai atkārtotas opcijas."
                    "خيارات غير مدعومة أو مكررة."
            | CliUsageIssue.Selector ->
                localized
                    language
                    "Unknown discovery selector."
                    "Nezināms apraksta atlasītājs."
                    "محدد استكشاف غير معروف."

        category
        + "\nClaimCore.Cli help\nClaimCore.Cli help describe\nClaimCore.Cli help schema\n"
