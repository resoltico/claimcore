namespace ClaimCore.Catalog

[<RequireQualifiedAccess>]
type CliUsageIssue =
    | Invocation
    | Topic
    | Options
    | Selector

/// Offline task guidance. Technical commands and names are never translated.
module CliHelp =
    let diagnosticRoutes =
        [
            InputDiagnostics.protocolId, InputDiagnostics.protocolId
            InputDiagnostics.configurationId, "configuration"
        ]

    let diagnosticTopics = diagnosticRoutes |> List.map fst

    let private topicFor topic =
        diagnosticRoutes
        |> List.tryFind (fst >> (=) topic)
        |> Option.map snd
        |> Option.defaultValue topic

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

    let private serviceForms language =
        [
            localized
                language
                "Service: canonical absolute HTTPS root with path exactly /. No user information, query or fragment; input is not normalized. Issuer: exact canonical HTTPS provider issuer; provider paths are allowed. Syntactic acceptance does not establish TLS/discovery/authentication success."
                "Pakalpojums: kanoniska absolūta HTTPS sakne ar ceļu tieši /. Bez lietotāja informācijas, vaicājuma vai fragmenta; ievade netiek normalizēta. Izdevējs: precīzs kanonisks HTTPS izdevējs; nodrošinātāja ceļi ir atļauti. Sintakses pieņemšana nepierāda TLS, atklāšanas vai autentifikācijas panākumus."
                "الخدمة: جذر HTTPS مطلق وقانوني بمسار / فقط، دون معلومات مستخدم أو استعلام أو جزء؛ لا يُطبّع الإدخال. المُصدر: عنوان HTTPS القانوني الدقيق للموفر؛ يُسمح بمسارات الموفر. قبول الصياغة لا يثبت نجاح TLS أو الاكتشاف أو المصادقة."
            "CLAIMCORE_SERVICE_URL accepted: https://localhost/"
            "CLAIMCORE_SERVICE_URL refused: https://localhost (missing explicit /)"
            "CLAIMCORE_SERVICE_URL refused: https://localhost/path (not a service root)"
            "CLAIMCORE_OIDC_ISSUER accepted syntax: https://identity.example/realms/claimcore"
        ]

    let private credentials language =
        [
            localized
                language
                "Client ID: nonblank, no surrounding whitespace; maximum UTF-16 code units: "
                "Klienta ID: nav tukšs, bez apkārtējām atstarpēm; maksimālais UTF-16 koda vienību skaits: "
                "معرّف العميل: غير فارغ ودون مسافات محيطة؛ الحد الأقصى لوحدات UTF-16: "
            + string CliConfigurationLimits.MaximumClientIdCodeUnits
            localized
                language
                "Mode: literal interactive (new-process browser PKCE) or automation (requires a secret FILE path). Never put secret content in argv, environment values or diagnostics."
                "Režīms: tieši interactive (jauna procesa pārlūka PKCE) vai automation (vajadzīgs noslēpuma FILE ceļš). Nekad nelieciet noslēpuma saturu argumentos, vides vērtībās vai diagnostikā."
                "الوضع: interactive حرفياً (PKCE في المتصفح لعملية جديدة) أو automation (يتطلب مسار FILE للسر). لا تضع محتوى السر في الوسائط أو قيم البيئة أو التشخيصات."
            localized
                language
                "Secret file: absolute admitted path; readable regular UTF-8 nonblank content. Existing whitespace treatment is unchanged. Maximum bytes: "
                "Noslēpuma fails: absolūts pieņemts ceļš; lasāms parasts fails ar netukšu UTF-8 saturu. Esošā atstarpju apstrāde nemainās. Maksimālais baitu skaits: "
                "ملف السر: مسار مطلق مقبول؛ ملف عادي قابل للقراءة بمحتوى UTF-8 غير فارغ. معالجة المسافات الحالية لا تتغير. الحد الأقصى للبايتات: "
            + string CliConfigurationLimits.MaximumSecretBytes
        ]

    let private privateInputs language =
        [
            localized
                language
                "Optional trust-root FILE paths: valid PEM CA roots. Omit for system trust. Overrides only for HTTPS loopback or .localhost endpoints; hostname and certificate checks remain active. Maximum bytes: "
                "Neobligātie uzticamības sakņu FILE ceļi: derīgas PEM CA saknes. Izlaidiet sistēmas uzticamībai. Aizstāšana tikai HTTPS lokālās cilpas vai .localhost galapunktiem; resursdatora un sertifikāta pārbaudes paliek aktīvas. Maksimālais baitu skaits: "
                "مسارات FILE الاختيارية لجذور الثقة: جذور CA صالحة بصيغة PEM. احذفها لاستخدام ثقة النظام. التجاوز فقط لنقاط HTTPS المحلية أو .localhost؛ تبقى فحوص اسم المضيف والشهادة مفعّلة. الحد الأقصى للبايتات: "
            + string CliConfigurationLimits.MaximumTrustRootBytes
            localized
                language
                "Private inputs: absolute valid-UTF-8 path without NUL; readable regular file, no other-user access or group write/execute. Group read is allowed: 0600 is sufficient, 0640 and 0440 can be admitted. This is not a universal chmod repair; Windows private-file runtime is unqualified. See docs/operations.md private-configuration for handle/symlink, ownership and ACL limits."
                "Privāta ievade: absolūts derīgs UTF-8 ceļš bez NUL; lasāms parasts fails, bez citu lietotāju piekļuves vai grupas rakstīšanas/izpildes. Grupas lasīšana ir atļauta: 0600 pietiek, var pieņemt arī 0640 un 0440. Tas nav universāls chmod labojums; Windows privāto failu izpilde nav kvalificēta. Par rokturu/saišu, īpašumtiesību un ACL robežām skatiet docs/operations.md private-configuration."
                "الإدخال الخاص: مسار UTF-8 مطلق صالح دون NUL؛ ملف عادي قابل للقراءة، دون وصول للآخرين أو كتابة/تنفيذ للمجموعة. قراءة المجموعة مسموحة: 0600 كافٍ، ويمكن قبول 0640 و0440. هذا ليس إصلاح chmod عاماً؛ تشغيل الملفات الخاصة على Windows غير مؤهل. راجع docs/operations.md private-configuration لحدود المقابض والروابط والملكية وACL."
            localized
                language
                "MISSING, INVALID and PRIVATE_INPUT_REFUSED are distinct known facts. This configuration attempt is NOT_SENT; it does not resolve earlier cross-process mutation uncertainty."
                "MISSING, INVALID un PRIVATE_INPUT_REFUSED ir atšķirīgi zināmi fakti. Šis konfigurācijas mēģinājums ir NOT_SENT; tas neatrisina agrāku mutācijas nenoteiktību starp procesiem."
                "MISSING وINVALID وPRIVATE_INPUT_REFUSED حقائق معروفة مختلفة. محاولة التهيئة هذه NOT_SENT؛ ولا تحل عدم يقين سابقاً بشأن تعديل عبر العمليات."
        ]

    let private configuration language =
        serviceForms language
        @ credentials language
        @ privateInputs language
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
        | topic when topic = InputDiagnostics.protocolId ->
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
            (common
             @ content language (topicFor topic) invocationBytes requestBytes responseBytes)
        + "\n"

    let diagnosticDiscovery language id invocationBytes requestBytes responseBytes =
        let topic = topicFor id

        if topic = "configuration" then
            "ClaimCore.Cli help " + topic + "\n"
        else
            text language id invocationBytes requestBytes responseBytes

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
