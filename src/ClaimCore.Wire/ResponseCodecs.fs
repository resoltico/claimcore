namespace ClaimCore.Wire

module ResponseCodecs =
    open Codec
    open ResponseScalars

    let private principal = RequestCodecs.principal
    let private scope = RequestCodecs.scope

    let caseFields =
        (record (fun v0 v1 v2 v3 v4 v5 v6 v7 v8 v9 v10 v11 v12 ->
            {
                CaseFields.IncidentDate = v0
                IncidentNotificationDate = v1
                IncidentCountry = v2
                ClaimantName = v3
                InsurerName = v4
                ClaimedAmount = v5
                ClaimedCurrency = v6
                CaseReference = v7
                PaymentDecisionDate = v8
                PayableAmount = v9
                PayableCurrency = v10
                PaymentDate = v11
                Status = v12
            })
        : Fields<CaseFields, _>)
        |> field "incidentDate" _.IncidentDate date
        |> field "incidentNotificationDate" _.IncidentNotificationDate date
        |> field "incidentCountry" _.IncidentCountry country
        |> field "claimantName" _.ClaimantName (nonempty)
        |> field "insurerName" _.InsurerName (nonempty)
        |> field "claimedAmount" _.ClaimedAmount amount
        |> field "claimedCurrency" _.ClaimedCurrency currency
        |> field "caseReference" _.CaseReference reference
        |> field "paymentDecisionDate" _.PaymentDecisionDate (nullable date)
        |> field "payableAmount" _.PayableAmount (nullable amount)
        |> field "payableCurrency" _.PayableCurrency (nullable currency)
        |> field "paymentDate" _.PaymentDate (nullable date)
        |> field "status" _.Status (tokens [ "OPENED"; "CLOSED" ])
        |> build
        |> named "CaseFields"

    let private liveStateFields =
        (record (fun disposition fields ->
            {
                LiveState.Disposition = disposition
                Fields = fields
            })
        : Fields<LiveState, _>)
        |> field "disposition" _.Disposition (tokens [ "ACTIVE"; "VOIDED" ])
        |> field "fields" _.Fields caseFields

    let private liveState =
        tagged "LiveState" "tag" [ variant "LIVE" id Some liveStateFields ]

    let caseState =
        tagged
            "CaseState"
            "tag"
            [
                variant
                    "LIVE"
                    CaseState.Live
                    (function
                    | CaseState.Live value -> Some value
                    | _ -> None)
                    liveStateFields
                unitVariant "ERASED" CaseState.Erased
            ]

    let caseSnapshot =
        (record (fun v0 v1 v2 ->
            {
                CaseSnapshot.CaseReference = v0
                Revision = v1
                State = v2
            })
        : Fields<CaseSnapshot, _>)
        |> field "caseReference" _.CaseReference reference
        |> field "revision" _.Revision (sequence)
        |> field "state" _.State (caseState)
        |> build
        |> named "CaseSnapshot"

    let correctionAdvice =
        (record (fun v0 v1 v2 ->
            {
                CorrectionAdvice.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<CorrectionAdvice, _>)
        |> field "registration" _.Registration (list 2 (tokens [ "KEEP"; "REPLACE" ]))
        |> field "decision" _.Decision (list 3 (tokens [ "KEEP"; "REPLACE"; "CLEAR" ]))
        |> field "payment" _.Payment (list 3 (tokens [ "KEEP"; "REPLACE"; "CLEAR" ]))
        |> build
        |> named "CorrectionAdvice"

    let private ordinaryOfferFields =
        record (fun act revision -> act, revision)
        |> field
            "act"
            fst
            (tokens
                [
                    "OPEN"
                    "AMEND_REGISTRATION"
                    "DECIDE"
                    "WITHDRAW_DECISION"
                    "RECORD_PAYMENT"
                    "CLEAR_PAYMENT"
                    "CLOSE"
                    "REOPEN"
                    "VOID"
                    "REINSTATE"
                    "ERASE"
                ])
        |> field "expectedRevision" snd revision

    let private correctionOfferFields =
        record (fun revision correction -> revision, correction)
        |> fixedField "act" "CORRECT_CASE"
        |> field "expectedRevision" fst revision
        |> field "correction" snd correctionAdvice

    let caseActOffer =
        choice
            "CaseActOffer"
            [
                branch
                    CaseActOffer.Act
                    (function
                    | CaseActOffer.Act(a, r) -> Some(a, r)
                    | _ -> None)
                    (ordinaryOfferFields |> build |> named "OrdinaryCaseActOffer")
                branch
                    CaseActOffer.Correct
                    (function
                    | CaseActOffer.Correct(r, c) -> Some(r, c)
                    | _ -> None)
                    (correctionOfferFields |> build |> named "CorrectionCaseActOffer")
            ]
        |> discriminated "act"

    let caseAdvice =
        (record (fun v0 v1 -> { CaseAdvice.Acts = v0; History = v1 }): Fields<CaseAdvice, _>)
        |> field "acts" _.Acts (list 12 caseActOffer)
        |> field "history" _.History (list 2 (tokens [ "SUMMARY"; "FULL" ]))
        |> build
        |> named "CaseAdvice"

    let caseView =
        (record (fun v0 v1 v2 v3 ->
            {
                CaseView.CaseReference = v0
                Revision = v1
                State = v2
                Advice = v3
            })
        : Fields<CaseView, _>)
        |> field "caseReference" _.CaseReference reference
        |> field "revision" _.Revision (sequence)
        |> field "state" _.State (caseState)
        |> field "advice" _.Advice (caseAdvice)
        |> build
        |> named "CaseView"

    let installationOffer =
        tagged
            "InstallationOffer"
            "kind"
            [
                variant
                    "OPEN"
                    InstallationOffer.Open
                    (function
                    | InstallationOffer.Open value -> Some value
                    | _ -> None)
                    (record id |> field "expectedRevision" id (integer 0L 0L))
                variant
                    "AUTHORITY_ACT"
                    InstallationOffer.AuthorityAct
                    (function
                    | InstallationOffer.AuthorityAct value -> Some value
                    | _ -> None)
                    (record id
                     |> field "act" id (tokens [ "REGISTER_ACTOR"; "SET_GRANT"; "SET_ENABLED" ]))
            ]

    let runtimeContext =
        (record (fun v0 v1 v2 ->
            {
                RuntimeContext.ProductVersion = v0
                BusinessDate = v1
                TimeZone = v2
            })
        : Fields<RuntimeContext, _>)
        |> field "productVersion" _.ProductVersion (nonempty)
        |> field "businessDate" _.BusinessDate date
        |> field "timeZone" _.TimeZone (nonempty)
        |> build
        |> named "RuntimeContext"

    let actorContext =
        (record (fun v0 v1 v2 v3 v4 v5 ->
            {
                ActorContext.ProductVersion = v0
                BusinessDate = v1
                TimeZone = v2
                Capabilities = v3
                Views = v4
                Offers = v5
            })
        : Fields<ActorContext, _>)
        |> field "productVersion" _.ProductVersion (nonempty)
        |> field "businessDate" _.BusinessDate date
        |> field "timeZone" _.TimeZone (nonempty)
        |> field "capabilities" _.Capabilities (list 16 capability)
        |> field "views" _.Views (list 3 (tokens [ "CASES"; "AUTHORITY"; "OPERATIONS" ]))
        |> field "offers" _.Offers (list 4 installationOffer)
        |> build
        |> named "ActorContext"

    let position =
        (record (fun v0 v1 ->
            {
                Position.Sequence = v0
                EntryHash = v1
            })
        : Fields<Position, _>)
        |> field "sequence" _.Sequence (sequence)
        |> field "entryHash" _.EntryHash digest
        |> build
        |> named "Position"

    let disclosure =
        tagged
            "Disclosure"
            "tag"
            [
                variant
                    "DISCLOSED"
                    Disclosure.Disclosed
                    (function
                    | Disclosure.Disclosed value -> Some value
                    | _ -> None)
                    (record id |> field "state" id liveState)
                unitVariant "WITHHELD" Disclosure.Withheld
                unitVariant "ERASED" Disclosure.Erased
            ]

    let caseReceiptFields =
        (record (fun v0 v1 v2 v3 v4 v5 v6 v7 ->
            {
                CaseReceipt.OperationId = v0
                Position = v1
                RecordedAt = v2
                RecordedBy = v3
                CaseReference = v4
                Revision = v5
                Act = v6
                Snapshot = v7
            })
        : Fields<CaseReceipt, _>)
        |> field "operationId" _.OperationId (uuid)
        |> field "position" _.Position (position)
        |> field "recordedAt" _.RecordedAt (instant)
        |> field "recordedBy" _.RecordedBy (uuid)
        |> fixedField "scope" "CASE"
        |> field "caseReference" _.CaseReference reference
        |> field "revision" _.Revision (sequence)
        |> field
            "act"
            _.Act
            (tokens
                [
                    "OPEN"
                    "AMEND_REGISTRATION"
                    "CORRECT_CASE"
                    "DECIDE"
                    "WITHDRAW_DECISION"
                    "RECORD_PAYMENT"
                    "CLEAR_PAYMENT"
                    "CLOSE"
                    "REOPEN"
                    "VOID"
                    "REINSTATE"
                    "ERASE"
                ])
        |> field "snapshot" _.Snapshot (disclosure)


    let caseReceipt = caseReceiptFields |> build |> named "CaseReceipt"

    let authorityReceiptFields =
        (record (fun v0 v1 v2 v3 v4 v5 ->
            {
                AuthorityReceipt.OperationId = v0
                Position = v1
                RecordedAt = v2
                RecordedBy = v3
                TargetActorId = v4
                Act = v5
            })
        : Fields<AuthorityReceipt, _>)
        |> field "operationId" _.OperationId (uuid)
        |> field "position" _.Position (position)
        |> field "recordedAt" _.RecordedAt (instant)
        |> field "recordedBy" _.RecordedBy (uuid)
        |> fixedField "scope" "AUTHORITY"
        |> field "targetActorId" _.TargetActorId (uuid)
        |> field
            "act"
            _.Act
            (tokens [ "PROVISION_OWNER"; "REGISTER_ACTOR"; "SET_GRANT"; "SET_ENABLED" ])


    let authorityReceipt = authorityReceiptFields |> build |> named "AuthorityReceipt"

    let changeSummary =
        (record (fun v0 v1 v2 v3 v4 v5 ->
            {
                ChangeSummary.OperationId = v0
                Position = v1
                Revision = v2
                Act = v3
                RecordedAt = v4
                RecordedBy = v5
            })
        : Fields<ChangeSummary, _>)
        |> field "operationId" _.OperationId (uuid)
        |> field "position" _.Position (position)
        |> field "revision" _.Revision (sequence)
        |> field
            "act"
            _.Act
            (tokens
                [
                    "OPEN"
                    "AMEND_REGISTRATION"
                    "CORRECT_CASE"
                    "DECIDE"
                    "WITHDRAW_DECISION"
                    "RECORD_PAYMENT"
                    "CLEAR_PAYMENT"
                    "CLOSE"
                    "REOPEN"
                    "VOID"
                    "REINSTATE"
                    "ERASE"
                ])
        |> field "recordedAt" _.RecordedAt (instant)
        |> field "recordedBy" _.RecordedBy (uuid)
        |> build
        |> named "ChangeSummary"

    let fieldChange =
        (record (fun v0 v1 v2 ->
            {
                FieldChange.Field = v0
                Before = v1
                After = v2
            })
        : Fields<FieldChange, _>)
        |> field "field" _.Field businessField
        |> field "before" _.Before (nullable nonempty)
        |> field "after" _.After (nullable nonempty)
        |> build
        |> named "FieldChange"

    let review =
        (record (fun v0 v1 v2 v3 ->
            {
                Review.Before = v0
                After = v1
                Changes = v2
                Context = v3
            })
        : Fields<Review, _>)
        |> field "before" _.Before (nullable caseSnapshot)
        |> field "after" _.After (caseSnapshot)
        |> field "changes" _.Changes (list 13 fieldChange)
        |> field "context" _.Context (runtimeContext)
        |> build
        |> named "Review"

    let actorSummary =
        (record (fun v0 v1 v2 ->
            {
                ActorSummary.ActorId = v0
                Principal = v1
                Enabled = v2
            })
        : Fields<ActorSummary, _>)
        |> field "actorId" _.ActorId (uuid)
        |> field "principal" _.Principal (principal)
        |> field "enabled" _.Enabled (boolean)
        |> build
        |> named "ActorSummary"

    let authorityGrant =
        (record (fun v0 v1 -> { AuthorityGrant.Role = v0; Scope = v1 }): Fields<AuthorityGrant, _>)
        |> field
            "role"
            _.Role
            (tokens
                [
                    "OWNER"
                    "CASE_READER"
                    "CASE_REGISTRAR"
                    "CASE_HANDLER"
                    "PAYMENT_RECORDER"
                    "DATA_STEWARD"
                    "AUDITOR"
                ])
        |> field "scope" _.Scope (scope)
        |> build
        |> named "AuthorityGrant"

    let grantsPage =
        (record (fun v0 v1 v2 ->
            {
                GrantsPage.Actor = v0
                Items = v1
                NextCursor = v2
            })
        : Fields<GrantsPage, _>)
        |> field "actor" _.Actor (actorSummary)
        |> field "items" _.Items (list 50 authorityGrant)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "GrantsPage"

    let actorsPage =
        (record (fun v0 v1 ->
            {
                ActorsPage.Items = v0
                NextCursor = v1
            })
        : Fields<ActorsPage, _>)
        |> field "items" _.Items (list 50 actorSummary)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "ActorsPage"

    let private summaryEntry =
        record id
        |> fixedField "tag" "SUMMARY"
        |> field "change" id changeSummary
        |> build

    let private fullEntry =
        record id |> fixedField "tag" "FULL" |> field "receipt" id caseReceipt |> build

    let historyEntries =
        choice
            "HistoryEntries"
            [
                branch
                    (fun () -> HistoryEntries.Empty)
                    (function
                    | HistoryEntries.Empty -> Some()
                    | _ -> None)
                    (boundedList 0 0 summaryEntry |> map (fun _ -> ()) (fun () -> []))
                branch
                    HistoryEntries.Summary
                    (function
                    | HistoryEntries.Summary values -> Some values
                    | _ -> None)
                    (boundedList 1 Limits.MaximumPageSize summaryEntry)
                branch
                    HistoryEntries.Full
                    (function
                    | HistoryEntries.Full values -> Some values
                    | _ -> None)
                    (boundedList 1 Limits.MaximumPageSize fullEntry)
            ]

    let historyPage =
        (record (fun v0 v1 ->
            {
                HistoryPage.Entries = v0
                NextCursor = v1
            })
        : Fields<HistoryPage, _>)
        |> field "entries" _.Entries historyEntries
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "HistoryPage"

    let session =
        (record (fun v0 v1 v2 ->
            {
                Session.Authenticated = v0
                WireFingerprint = v1
                AntiforgeryToken = v2
            })
        : Fields<Session, _>)
        |> field "authenticated" _.Authenticated (boolean)
        |> field "wireFingerprint" _.WireFingerprint digest
        |> field "antiforgeryToken" _.AntiforgeryToken (nullable nonempty)
        |> build
        |> named "Session"

    let outcomeUnknown =
        (record (fun v0 -> { OutcomeUnknown.OperationId = v0 }): Fields<OutcomeUnknown, _>)
        |> field "operationId" _.OperationId (uuid)
        |> build
        |> named "OutcomeUnknown"

    let reviewed =
        (record (fun v0 -> { Reviewed.Review = v0 }): Fields<Reviewed, _>)
        |> field "review" _.Review (review)
        |> build
        |> named "Reviewed"

    let emptyParameters = record () |> build |> named "EmptyParameters"

    let textLengthParameters =
        (record (fun v0 ->
            {
                TextLengthParameters.MaximumCharacters = v0
            })
        : Fields<TextLengthParameters, _>)
        |> field "maximumCharacters" _.MaximumCharacters (integer 0L 2147483647L)
        |> build
        |> named "TextLengthParameters"

    let decimalParameters =
        (record (fun v0 v1 ->
            {
                DecimalParameters.MaximumIntegerDigits = v0
                MaximumFractionalDigits = v1
            })
        : Fields<DecimalParameters, _>)
        |> field "maximumIntegerDigits" _.MaximumIntegerDigits (integer 1L 2147483647L)
        |> field "maximumFractionalDigits" _.MaximumFractionalDigits (integer 0L 2147483647L)
        |> build
        |> named "DecimalParameters"

    let pageParameters =
        (record (fun v0 -> { PageParameters.MaximumPageSize = v0 }): Fields<PageParameters, _>)
        |> field "maximumPageSize" _.MaximumPageSize (integer 1L 2147483647L)
        |> build
        |> named "PageParameters"

    let private diagnostics0 =
        [
            variant
                "INPUT_TEXT_REQUIRED"
                Diagnostic.TextRequired
                (function
                | Diagnostic.TextRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "INPUT_MALFORMED_UNICODE"
                Diagnostic.MalformedUnicode
                (function
                | Diagnostic.MalformedUnicode value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "INPUT_SURROUNDING_WHITESPACE"
                Diagnostic.SurroundingWhitespace
                (function
                | Diagnostic.SurroundingWhitespace value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "INPUT_TEXT_TOO_LONG"
                Diagnostic.TextTooLong
                (function
                | Diagnostic.TextTooLong value -> Some value
                | _ -> None)
                (record id |> field "parameters" id textLengthParameters)
            variant
                "INPUT_CONTROL_CHARACTERS"
                Diagnostic.ControlCharacters
                (function
                | Diagnostic.ControlCharacters value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "TEXT_FORMATTING_CHARACTER"
                Diagnostic.FormattingCharacter
                (function
                | Diagnostic.FormattingCharacter value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics1 =
        [
            variant
                "INPUT_CASE_REFERENCE_FORMAT"
                Diagnostic.ReferenceFormat
                (function
                | Diagnostic.ReferenceFormat value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "INPUT_COUNTRY_FORMAT"
                Diagnostic.CountryFormat
                (function
                | Diagnostic.CountryFormat value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "INPUT_CALENDAR_DATE_REQUIRED"
                Diagnostic.CalendarDateRequired
                (function
                | Diagnostic.CalendarDateRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "INPUT_DATE_ORDER"
                Diagnostic.ChronologicalOrder
                (function
                | Diagnostic.ChronologicalOrder value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "INPUT_FUTURE_DATE"
                Diagnostic.FutureDate
                (function
                | Diagnostic.FutureDate value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "INPUT_DECIMAL_FORMAT"
                Diagnostic.DecimalFormat
                (function
                | Diagnostic.DecimalFormat value -> Some value
                | _ -> None)
                (record id |> field "parameters" id decimalParameters)
        ]

    let private diagnostics2 =
        [
            variant
                "INPUT_CURRENCY_FORMAT"
                Diagnostic.CurrencyFormat
                (function
                | Diagnostic.CurrencyFormat value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "COMMAND_OPERATION_ID_REQUIRED"
                Diagnostic.EmptyOperationId
                (function
                | Diagnostic.EmptyOperationId value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "COMMAND_EXPECTED_REVISION_RANGE"
                Diagnostic.ExpectedRevisionOutOfRange
                (function
                | Diagnostic.ExpectedRevisionOutOfRange value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "REVISION_EXHAUSTED"
                Diagnostic.RevisionExhausted
                (function
                | Diagnostic.RevisionExhausted value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CORRECTION_PAYMENT_CLEAR_REQUIRED"
                Diagnostic.PaymentClearRequired
                (function
                | Diagnostic.PaymentClearRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CORRECTION_PAYMENT_ACKNOWLEDGEMENT_REQUIRED"
                Diagnostic.PaymentAcknowledgementRequired
                (function
                | Diagnostic.PaymentAcknowledgementRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics3 =
        [
            variant
                "CASE_ALREADY_EXISTS"
                Diagnostic.CaseAlreadyExists
                (function
                | Diagnostic.CaseAlreadyExists value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_REVISION_CONFLICT"
                Diagnostic.RevisionConflict
                (function
                | Diagnostic.RevisionConflict value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_CLOSED"
                Diagnostic.CaseClosed
                (function
                | Diagnostic.CaseClosed value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_ALREADY_CLOSED"
                Diagnostic.AlreadyClosed
                (function
                | Diagnostic.AlreadyClosed value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_ALREADY_OPENED"
                Diagnostic.AlreadyOpened
                (function
                | Diagnostic.AlreadyOpened value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_AMENDMENT_REQUIRES_UNDECIDED"
                Diagnostic.AmendmentRequiresUndecided
                (function
                | Diagnostic.AmendmentRequiresUndecided value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics4 =
        [
            variant
                "CORRECTION_NO_CHANGES"
                Diagnostic.CorrectionNoChanges
                (function
                | Diagnostic.CorrectionNoChanges value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CORRECTION_EXISTING_VALUE_REQUIRED"
                Diagnostic.CorrectionRequiresExistingValue
                (function
                | Diagnostic.CorrectionRequiresExistingValue value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_DECISION_REQUIRED"
                Diagnostic.DecisionRequired
                (function
                | Diagnostic.DecisionRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_PAYMENT_ALREADY_RECORDED"
                Diagnostic.PaymentAlreadyRecorded
                (function
                | Diagnostic.PaymentAlreadyRecorded value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_PAYMENT_NOT_RECORDED"
                Diagnostic.PaymentNotRecorded
                (function
                | Diagnostic.PaymentNotRecorded value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_DECISION_ALREADY_PAID"
                Diagnostic.DecisionAlreadyPaid
                (function
                | Diagnostic.DecisionAlreadyPaid value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics5 =
        [
            variant
                "CASE_ZERO_DECISION_CANNOT_BE_PAID"
                Diagnostic.ZeroDecisionCannotBePaid
                (function
                | Diagnostic.ZeroDecisionCannotBePaid value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "QUERY_PAGE_LIMIT_RANGE"
                Diagnostic.PageLimitOutOfRange
                (function
                | Diagnostic.PageLimitOutOfRange value -> Some value
                | _ -> None)
                (record id |> field "parameters" id pageParameters)
            variant
                "QUERY_CURSOR_INVALID"
                Diagnostic.InvalidCursor
                (function
                | Diagnostic.InvalidCursor value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "ACT_NOT_PERMITTED"
                Diagnostic.ActNotPermitted
                (function
                | Diagnostic.ActNotPermitted value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "LEDGER_SEQUENCE_EXHAUSTED"
                Diagnostic.LedgerExhausted
                (function
                | Diagnostic.LedgerExhausted value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "OPERATION_CONTENT_CONFLICT"
                Diagnostic.IdempotencyConflict
                (function
                | Diagnostic.IdempotencyConflict value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics6 =
        [
            variant
                "ACCESS_RESOURCE_UNAVAILABLE"
                Diagnostic.ResourceUnavailable
                (function
                | Diagnostic.ResourceUnavailable value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_VOIDED"
                Diagnostic.CaseVoided
                (function
                | Diagnostic.CaseVoided value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "CASE_ERASED"
                Diagnostic.CaseErased
                (function
                | Diagnostic.CaseErased value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "ALREADY_VOIDED"
                Diagnostic.AlreadyVoided
                (function
                | Diagnostic.AlreadyVoided value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "NOT_VOIDED"
                Diagnostic.NotVoided
                (function
                | Diagnostic.NotVoided value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "LAST_OWNER"
                Diagnostic.LastOwner
                (function
                | Diagnostic.LastOwner value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics7 =
        [
            variant
                "ACTOR_ALREADY_REGISTERED"
                Diagnostic.ActorAlreadyRegistered
                (function
                | Diagnostic.ActorAlreadyRegistered value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "ACTOR_UNKNOWN"
                Diagnostic.ActorUnknown
                (function
                | Diagnostic.ActorUnknown value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                "HUMAN_PRINCIPAL_REQUIRED"
                Diagnostic.HumanPrincipalRequired
                (function
                | Diagnostic.HumanPrincipalRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let diagnostic =
        tagged
            "Diagnostic"
            "id"
            (diagnostics0
             @ diagnostics1
             @ diagnostics2
             @ diagnostics3
             @ diagnostics4
             @ diagnostics5
             @ diagnostics6
             @ diagnostics7)

    let rejection =
        (record (fun v0 v1 v2 v3 v4 ->
            {
                Rejection.Diagnostic = v0
                Message = v1
                Field = v2
                ActualRevision = v3
                RecommendedAction = v4
            })
        : Fields<Rejection, _>)
        |> field "diagnostic" _.Diagnostic (diagnostic)
        |> field "message" _.Message (nonempty)
        |> field
            "field"
            _.Field
            (nullable (
                tokens
                    [
                        "incidentDate"
                        "incidentNotificationDate"
                        "incidentCountry"
                        "claimantName"
                        "insurerName"
                        "claimedAmount"
                        "claimedCurrency"
                        "caseReference"
                        "paymentDecisionDate"
                        "payableAmount"
                        "payableCurrency"
                        "paymentDate"
                        "operationId"
                        "expectedRevision"
                        "payment"
                        "reason"
                        "limit"
                        "cursor"
                    ]
            ))
        |> field "actualRevision" _.ActualRevision (nullable sequence)
        |> field
            "recommendedAction"
            _.RecommendedAction
            (tokens [ "CORRECT_INPUT"; "READ_CURRENT"; "STOP_AND_INVESTIGATE"; "NONE_REQUIRED" ])
        |> build
        |> named "Rejection"

    let private faultVariant reason id action =
        let diagnostic =
            record (fun () -> ())
            |> fixedField "id" id
            |> field "parameters" (fun () -> ()) emptyParameters
            |> build

        let fields =
            record (fun () message ->
                {
                    Fault.Reason = reason
                    Message = message
                })
            |> field "diagnostic" (fun _ -> ()) diagnostic
            |> field "message" _.Message nonempty
            |> fixedField "recommendedAction" action

        branch
            (fun value -> value)
            (fun value -> if value.Reason = reason then Some value else None)
            (fields |> build)

    let fault =
        choice
            "Fault"
            [
                faultVariant FaultReason.InternalError "CORE_INTERNAL_ERROR" "STOP_AND_INVESTIGATE"
                faultVariant FaultReason.StoreUnavailable "CORE_STORE_UNAVAILABLE" "RETRY_SAFE"
                faultVariant
                    FaultReason.StoreIntegrityError
                    "CORE_STORE_INTEGRITY_ERROR"
                    "STOP_AND_INVESTIGATE"
                faultVariant
                    FaultReason.SchemaMismatch
                    "CORE_SCHEMA_MISMATCH"
                    "STOP_AND_INVESTIGATE"
                faultVariant
                    FaultReason.OperationContentConflict
                    "CORE_OPERATION_CONTENT_CONFLICT"
                    "STOP_AND_INVESTIGATE"
            ]

    let receipt =
        choice
            "Receipt"
            [
                branch
                    Receipt.Case
                    (function
                    | Receipt.Case value -> Some value
                    | _ -> None)
                    caseReceipt
                branch
                    Receipt.Authority
                    (function
                    | Receipt.Authority value -> Some value
                    | _ -> None)
                    authorityReceipt
            ]
        |> discriminated "scope"

    let caseSummary =
        tagged
            "CaseSummary"
            "state"
            [
                variant
                    "LIVE"
                    CaseSummary.Live
                    (function
                    | CaseSummary.Live value -> Some value
                    | _ -> None)
                    ((record (fun reference revision status disposition ->
                        {
                            LiveSummary.CaseReference = reference
                            Revision = revision
                            Status = status
                            Disposition = disposition
                        })
                     : Fields<LiveSummary, _>)
                     |> field "caseReference" _.CaseReference reference
                     |> field "revision" _.Revision sequence
                     |> field "status" _.Status (tokens [ "OPENED"; "CLOSED" ])
                     |> field "disposition" _.Disposition (tokens [ "ACTIVE"; "VOIDED" ]))
                variant
                    "ERASED"
                    CaseSummary.Erased
                    (function
                    | CaseSummary.Erased value -> Some value
                    | _ -> None)
                    ((record (fun reference revision ->
                        {
                            ErasedSummary.CaseReference = reference
                            Revision = revision
                        })
                     : Fields<ErasedSummary, _>)
                     |> field "caseReference" _.CaseReference reference
                     |> field "revision" _.Revision sequence)
            ]

    let casesPage =
        (record (fun v0 v1 v2 ->
            {
                CasesPage.Items = v0
                NextCursor = v1
                Offers = v2
            })
        : Fields<CasesPage, _>)
        |> field "items" _.Items (list 50 caseSummary)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> field "offers" _.Offers (list 4 installationOffer)
        |> build
        |> named "CasesPage"

    let sessionOutcome =
        tagged
            "SessionOutcome"
            "tag"
            [
                variant
                    "SUCCEEDED"
                    SessionOutcome.Succeeded
                    (fun (SessionOutcome.Succeeded value) -> Some value)
                    (record id |> field "data" id session)
            ]

    let sessionReply =
        (record (fun v0 -> { SessionReply.Outcome = v0 }): Fields<SessionReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.Session)
        |> field "outcome" _.Outcome (sessionOutcome)
        |> build
        |> named "SessionReply"

    let sessionLogoutOutcome =
        tagged
            "SessionLogoutOutcome"
            "tag"
            [
                variant
                    "SUCCEEDED"
                    SessionLogoutOutcome.Succeeded
                    (fun (SessionLogoutOutcome.Succeeded value) -> Some value)
                    (record id |> field "data" id nullValue)
            ]

    let sessionLogoutReply =
        (record (fun v0 -> { SessionLogoutReply.Outcome = v0 }): Fields<SessionLogoutReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.SessionLogout)
        |> field "outcome" _.Outcome (sessionLogoutOutcome)
        |> build
        |> named "SessionLogoutReply"

    let contextOutcome =
        tagged
            "ContextOutcome"
            "tag"
            [
                variant
                    "SUCCEEDED"
                    ContextOutcome.Succeeded
                    (function
                    | ContextOutcome.Succeeded value -> Some value
                    | _ -> None)
                    (record id |> field "data" id actorContext)
                variant
                    "REJECTED"
                    ContextOutcome.Rejected
                    (function
                    | ContextOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    ContextOutcome.Failed
                    (function
                    | ContextOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    ContextOutcome.Cancelled
                    (function
                    | ContextOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let contextReply =
        (record (fun v0 -> { ContextReply.Outcome = v0 }): Fields<ContextReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.Context)
        |> field "outcome" _.Outcome (contextOutcome)
        |> build
        |> named "ContextReply"

    let caseGetOutcome =
        tagged
            "CaseGetOutcome"
            "tag"
            [
                variant
                    "SUCCEEDED"
                    CaseGetOutcome.Succeeded
                    (function
                    | CaseGetOutcome.Succeeded value -> Some value
                    | _ -> None)
                    (record id |> field "data" id caseView)
                variant
                    "REJECTED"
                    CaseGetOutcome.Rejected
                    (function
                    | CaseGetOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    CaseGetOutcome.Failed
                    (function
                    | CaseGetOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    CaseGetOutcome.Cancelled
                    (function
                    | CaseGetOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let caseGetReply =
        (record (fun v0 -> { CaseGetReply.Outcome = v0 }): Fields<CaseGetReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.CaseGet)
        |> field "outcome" _.Outcome (caseGetOutcome)
        |> build
        |> named "CaseGetReply"

    let caseListOutcome =
        tagged
            "CaseListOutcome"
            "tag"
            [
                variant
                    "SUCCEEDED"
                    CaseListOutcome.Succeeded
                    (function
                    | CaseListOutcome.Succeeded value -> Some value
                    | _ -> None)
                    (record id |> field "data" id casesPage)
                variant
                    "REJECTED"
                    CaseListOutcome.Rejected
                    (function
                    | CaseListOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    CaseListOutcome.Failed
                    (function
                    | CaseListOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    CaseListOutcome.Cancelled
                    (function
                    | CaseListOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let caseListReply =
        (record (fun v0 -> { CaseListReply.Outcome = v0 }): Fields<CaseListReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.CaseList)
        |> field "outcome" _.Outcome (caseListOutcome)
        |> build
        |> named "CaseListReply"

    let caseHistoryOutcome =
        tagged
            "CaseHistoryOutcome"
            "tag"
            [
                variant
                    "SUCCEEDED"
                    CaseHistoryOutcome.Succeeded
                    (function
                    | CaseHistoryOutcome.Succeeded value -> Some value
                    | _ -> None)
                    (record id |> field "data" id historyPage)
                variant
                    "REJECTED"
                    CaseHistoryOutcome.Rejected
                    (function
                    | CaseHistoryOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    CaseHistoryOutcome.Failed
                    (function
                    | CaseHistoryOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    CaseHistoryOutcome.Cancelled
                    (function
                    | CaseHistoryOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let caseHistoryReply =
        (record (fun v0 -> { CaseHistoryReply.Outcome = v0 }): Fields<CaseHistoryReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.CaseHistory)
        |> field "outcome" _.Outcome (caseHistoryOutcome)
        |> build
        |> named "CaseHistoryReply"

    let casePreviewOutcome =
        tagged
            "CasePreviewOutcome"
            "tag"
            [
                variant
                    "REVIEWED"
                    CasePreviewOutcome.Reviewed
                    (function
                    | CasePreviewOutcome.Reviewed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id reviewed)
                variant
                    "ALREADY_ACCEPTED"
                    CasePreviewOutcome.AlreadyAccepted
                    (function
                    | CasePreviewOutcome.AlreadyAccepted value -> Some value
                    | _ -> None)
                    (record id |> field "data" id caseReceipt)
                variant
                    "REJECTED"
                    CasePreviewOutcome.Rejected
                    (function
                    | CasePreviewOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    CasePreviewOutcome.Failed
                    (function
                    | CasePreviewOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    CasePreviewOutcome.Cancelled
                    (function
                    | CasePreviewOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let casePreviewReply =
        (record (fun v0 v1 ->
            {
                CasePreviewReply.OperationId = v0
                Outcome = v1
            })
        : Fields<CasePreviewReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.CasePreview)
        |> field "operationId" _.OperationId (uuid)
        |> field "outcome" _.Outcome (casePreviewOutcome)
        |> build
        |> named "CasePreviewReply"

    let caseExecuteOutcome =
        tagged
            "CaseExecuteOutcome"
            "tag"
            [
                variant
                    "ACCEPTED"
                    CaseExecuteOutcome.Accepted
                    (function
                    | CaseExecuteOutcome.Accepted value -> Some value
                    | _ -> None)
                    (record id |> field "data" id caseReceipt)
                variant
                    "ALREADY_ACCEPTED"
                    CaseExecuteOutcome.AlreadyAccepted
                    (function
                    | CaseExecuteOutcome.AlreadyAccepted value -> Some value
                    | _ -> None)
                    (record id |> field "data" id caseReceipt)
                variant
                    "OUTCOME_UNKNOWN"
                    CaseExecuteOutcome.OutcomeUnknown
                    (function
                    | CaseExecuteOutcome.OutcomeUnknown value -> Some value
                    | _ -> None)
                    (record id |> field "data" id outcomeUnknown)
                variant
                    "REJECTED"
                    CaseExecuteOutcome.Rejected
                    (function
                    | CaseExecuteOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    CaseExecuteOutcome.Failed
                    (function
                    | CaseExecuteOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    CaseExecuteOutcome.Cancelled
                    (function
                    | CaseExecuteOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let caseExecuteReply =
        (record (fun v0 v1 ->
            {
                CaseExecuteReply.OperationId = v0
                Outcome = v1
            })
        : Fields<CaseExecuteReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.CaseExecute)
        |> field "operationId" _.OperationId (uuid)
        |> field "outcome" _.Outcome (caseExecuteOutcome)
        |> build
        |> named "CaseExecuteReply"

    let operationObserveOutcome =
        tagged
            "OperationObserveOutcome"
            "tag"
            [
                variant
                    "SUCCEEDED"
                    OperationObserveOutcome.Succeeded
                    (function
                    | OperationObserveOutcome.Succeeded value -> Some value
                    | _ -> None)
                    (record id |> field "data" id receipt)
                variant
                    "REJECTED"
                    OperationObserveOutcome.Rejected
                    (function
                    | OperationObserveOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    OperationObserveOutcome.Failed
                    (function
                    | OperationObserveOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    OperationObserveOutcome.Cancelled
                    (function
                    | OperationObserveOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let operationObserveReply =
        (record (fun v0 -> { OperationObserveReply.Outcome = v0 }): Fields<OperationObserveReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.OperationObserve)
        |> field "outcome" _.Outcome (operationObserveOutcome)
        |> build
        |> named "OperationObserveReply"

    let authorityListOutcome =
        tagged
            "AuthorityListOutcome"
            "tag"
            [
                variant
                    "SUCCEEDED"
                    AuthorityListOutcome.Succeeded
                    (function
                    | AuthorityListOutcome.Succeeded value -> Some value
                    | _ -> None)
                    (record id |> field "data" id actorsPage)
                variant
                    "REJECTED"
                    AuthorityListOutcome.Rejected
                    (function
                    | AuthorityListOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    AuthorityListOutcome.Failed
                    (function
                    | AuthorityListOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    AuthorityListOutcome.Cancelled
                    (function
                    | AuthorityListOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let authorityListReply =
        (record (fun v0 -> { AuthorityListReply.Outcome = v0 }): Fields<AuthorityListReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.AuthorityList)
        |> field "outcome" _.Outcome (authorityListOutcome)
        |> build
        |> named "AuthorityListReply"

    let authorityGrantsOutcome =
        tagged
            "AuthorityGrantsOutcome"
            "tag"
            [
                variant
                    "SUCCEEDED"
                    AuthorityGrantsOutcome.Succeeded
                    (function
                    | AuthorityGrantsOutcome.Succeeded value -> Some value
                    | _ -> None)
                    (record id |> field "data" id grantsPage)
                variant
                    "REJECTED"
                    AuthorityGrantsOutcome.Rejected
                    (function
                    | AuthorityGrantsOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    AuthorityGrantsOutcome.Failed
                    (function
                    | AuthorityGrantsOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    AuthorityGrantsOutcome.Cancelled
                    (function
                    | AuthorityGrantsOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let authorityGrantsReply =
        (record (fun v0 -> { AuthorityGrantsReply.Outcome = v0 }): Fields<AuthorityGrantsReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.AuthorityGrants)
        |> field "outcome" _.Outcome (authorityGrantsOutcome)
        |> build
        |> named "AuthorityGrantsReply"

    let authorityExecuteOutcome =
        tagged
            "AuthorityExecuteOutcome"
            "tag"
            [
                variant
                    "ACCEPTED"
                    AuthorityExecuteOutcome.Accepted
                    (function
                    | AuthorityExecuteOutcome.Accepted value -> Some value
                    | _ -> None)
                    (record id |> field "data" id authorityReceipt)
                variant
                    "ALREADY_ACCEPTED"
                    AuthorityExecuteOutcome.AlreadyAccepted
                    (function
                    | AuthorityExecuteOutcome.AlreadyAccepted value -> Some value
                    | _ -> None)
                    (record id |> field "data" id authorityReceipt)
                variant
                    "OUTCOME_UNKNOWN"
                    AuthorityExecuteOutcome.OutcomeUnknown
                    (function
                    | AuthorityExecuteOutcome.OutcomeUnknown value -> Some value
                    | _ -> None)
                    (record id |> field "data" id outcomeUnknown)
                variant
                    "REJECTED"
                    AuthorityExecuteOutcome.Rejected
                    (function
                    | AuthorityExecuteOutcome.Rejected value -> Some value
                    | _ -> None)
                    (record id |> field "data" id rejection)
                variant
                    "FAILED"
                    AuthorityExecuteOutcome.Failed
                    (function
                    | AuthorityExecuteOutcome.Failed value -> Some value
                    | _ -> None)
                    (record id |> field "data" id fault)
                variant
                    "CANCELLED"
                    AuthorityExecuteOutcome.Cancelled
                    (function
                    | AuthorityExecuteOutcome.Cancelled value -> Some value
                    | _ -> None)
                    (record id |> field "data" id nullValue)
            ]

    let authorityExecuteReply =
        (record (fun v0 v1 ->
            {
                AuthorityExecuteReply.OperationId = v0
                Outcome = v1
            })
        : Fields<AuthorityExecuteReply, _>)
        |> fixedField "endpoint" (Endpoint.id Endpoint.AuthorityExecute)
        |> field "operationId" _.OperationId (uuid)
        |> field "outcome" _.Outcome (authorityExecuteOutcome)
        |> build
        |> named "AuthorityExecuteReply"
