namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract
open System
open System.Globalization

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
        |> field
            "status"
            _.Status
            (enumeration (
                CaseStatuses.all |> List.map (fun status -> status, CaseStatuses.token status)
            ))
        |> build
        |> named "CaseFields"

    let private liveStateFields =
        Codec.record (fun disposition fields -> fields, disposition)
        |> field
            "disposition"
            snd
            (enumeration [ Disposition.Active, "ACTIVE"; Disposition.Voided, "VOIDED" ])
        |> field "fields" fst caseFields

    let private liveVariant =
        variant
            "LIVE"
            CaseView.Live
            (function
            | CaseView.Live(fields, disposition) -> Some(fields, disposition)
            | _ -> None)
            liveStateFields

    let private liveState = tagged "LiveState" "tag" [ liveVariant ]

    let caseState =
        tagged "CaseState" "tag" [ liveVariant; unitVariant "ERASED" CaseView.Erased ]

    let caseSnapshot =
        (record (fun reference revision view ->
            {
                Reference = reference
                Revision = revision
                View = view
            })
        : Fields<CaseSnapshot, _>)
        |> field "caseReference" _.Reference reference
        |> field "revision" _.Revision sequence
        |> field "state" _.View caseState
        |> build
        |> named "CaseSnapshot"

    let private correctionAction =
        enumeration
            [
                CorrectionGroupAction.Keep, "KEEP"
                CorrectionGroupAction.Replace, "REPLACE"
                CorrectionGroupAction.Clear, "CLEAR"
            ]

    let private historyDetail =
        enumeration [ HistoryDetail.Summary, "SUMMARY"; HistoryDetail.Full, "FULL" ]

    let private authorityKind =
        enumeration
            [
                AuthorityKind.RegisterActor, "REGISTER_ACTOR"
                AuthorityKind.SetGrant, "SET_GRANT"
                AuthorityKind.SetEnabled, "SET_ENABLED"
            ]

    let private dateValue =
        date
        |> map
            (fun value -> DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            (fun value -> value.ToString("O", CultureInfo.InvariantCulture))

    let correctionAdvice =
        (record (fun registration decision payment ->
            {
                Registration = registration
                Decision = decision
                Payment = payment
            })
        : Fields<CorrectionChoices, _>)
        |> field
            "registration"
            _.Registration
            (list
                2
                (enumeration
                    [ CorrectionGroupAction.Keep, "KEEP"; CorrectionGroupAction.Replace, "REPLACE" ]))
        |> field "decision" _.Decision (list 3 correctionAction)
        |> field "payment" _.Payment (list 3 correctionAction)
        |> build
        |> named "CorrectionAdvice"

    let private ordinaryOfferFields =
        record (fun act revision -> act, revision)
        |> field
            "act"
            fst
            (enumeration (
                CaseActKinds.all
                |> List.filter ((<>) (CaseActKind.Business BusinessActKind.CorrectCase))
                |> List.map (fun act -> act, CaseActKinds.token act)
            ))
        |> field "expectedRevision" snd revision

    let private correctionOfferFields =
        record (fun revision correction -> revision, correction)
        |> fixedField "act" (CaseActKinds.token (CaseActKind.Business BusinessActKind.CorrectCase))
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
                    CaseActOffer.CorrectCase
                    (function
                    | CaseActOffer.CorrectCase(r, c) -> Some(r, c)
                    | _ -> None)
                    (correctionOfferFields |> build |> named "CorrectionCaseActOffer")
            ]
        |> discriminated "act"

    let caseAdvice =
        (record (fun v0 v1 -> { CaseAdvice.Acts = v0; History = v1 }): Fields<CaseAdvice, _>)
        |> field "acts" _.Acts (list 12 caseActOffer)
        |> field "history" _.History (list 2 historyDetail)
        |> build
        |> named "CaseAdvice"

    let caseView =
        (record (fun reference revision view advice ->
            {
                Snapshot =
                    {
                        Reference = reference
                        Revision = revision
                        View = view
                    }
                Advice = advice
            })
        : Fields<CaseReadModel, _>)
        |> field "caseReference" _.Snapshot.Reference reference
        |> field "revision" _.Snapshot.Revision sequence
        |> field "state" _.Snapshot.View caseState
        |> field "advice" _.Advice caseAdvice
        |> build
        |> named "CaseView"

    let installationOffer =
        tagged
            "InstallationOffer"
            "kind"
            [
                variant
                    "OPEN"
                    (fun () -> InstallationOffer.Open)
                    (function
                    | InstallationOffer.Open -> Some()
                    | _ -> None)
                    (record (fun _ -> ()) |> field "expectedRevision" (fun () -> 0L) (integer 0L 0L))
                variant
                    "AUTHORITY_ACT"
                    InstallationOffer.AuthorityAct
                    (function
                    | InstallationOffer.AuthorityAct act -> Some act
                    | _ -> None)
                    (record id |> field "act" id authorityKind)
            ]

    let runtimeContext =
        (record (fun version date zone ->
            {
                ProductVersion = version
                EffectiveBusinessDate = date
                TimeZoneId = zone
            })
        : Fields<RuntimeContext, _>)
        |> field "productVersion" _.ProductVersion nonempty
        |> field "businessDate" _.EffectiveBusinessDate dateValue
        |> field "timeZone" _.TimeZoneId nonempty
        |> build
        |> named "RuntimeContext"

    let actorContext =
        (record (fun version date zone capabilities views offers ->
            {
                Runtime =
                    {
                        ProductVersion = version
                        EffectiveBusinessDate = date
                        TimeZoneId = zone
                    }
                Capabilities = capabilities
                Views = views
                Offers = offers
            })
        : Fields<ActorContext, _>)
        |> field "productVersion" _.Runtime.ProductVersion nonempty
        |> field "businessDate" _.Runtime.EffectiveBusinessDate dateValue
        |> field "timeZone" _.Runtime.TimeZoneId nonempty
        |> field
            "capabilities"
            _.Capabilities
            (list
                16
                (enumeration (
                    Capabilities.all |> List.map (fun value -> value, Capabilities.token value)
                )))
        |> field
            "views"
            _.Views
            (list
                3
                (enumeration
                    [
                        View.Cases, "CASES"
                        View.Authority, "AUTHORITY"
                        View.Operations, "OPERATIONS"
                    ]))
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
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.Open))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.AmendRegistration))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.CorrectCase))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.Decide))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.WithdrawDecision))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.RecordPayment))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.ClearPayment))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.Close))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.Reopen))
                    (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Void))
                    (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Reinstate))
                    (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Erase))
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
            (tokens
                [
                    (AuthorityKinds.token AuthorityKind.ProvisionOwner)
                    (AuthorityKinds.token AuthorityKind.RegisterActor)
                    (AuthorityKinds.token AuthorityKind.SetGrant)
                    (AuthorityKinds.token AuthorityKind.SetEnabled)
                ])


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
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.Open))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.AmendRegistration))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.CorrectCase))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.Decide))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.WithdrawDecision))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.RecordPayment))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.ClearPayment))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.Close))
                    (CaseActKinds.token (CaseActKind.Business BusinessActKind.Reopen))
                    (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Void))
                    (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Reinstate))
                    (CaseActKinds.token (CaseActKind.Stewardship StewardshipActKind.Erase))
                ])
        |> field "recordedAt" _.RecordedAt (instant)
        |> field "recordedBy" _.RecordedBy (uuid)
        |> build
        |> named "ChangeSummary"

    let fieldChange =
        (record (fun field before after ->
            {
                FieldName = field
                Before = before
                After = after
            })
        : Fields<FieldDiff, _>)
        |> field "field" _.FieldName (enumeration BusinessField.all)
        |> field "before" _.Before (nullable nonempty)
        |> field "after" _.After (nullable nonempty)
        |> build
        |> named "FieldChange"

    let review =
        (record (fun before proposed changes context ->
            {
                Before = before
                Proposed = proposed
                Changes = changes
                Context = context
            })
        : Fields<AdvisoryReview, _>)
        |> field "before" _.Before (nullable caseSnapshot)
        |> field "after" _.Proposed caseSnapshot
        |> field "changes" _.Changes (list FieldTable.rows.Length fieldChange)
        |> field "context" _.Context runtimeContext
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
        |> field "role" _.Role (tokens (RoleTokens.all |> List.map RoleTokens.token))
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
                ContractIdentity = v1
                AntiforgeryToken = v2
            })
        : Fields<Session, _>)
        |> field "authenticated" _.Authenticated (boolean)
        |> field "contractIdentity" _.ContractIdentity digest
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

    let private argumentCodec argument =
        let definition = DiagnosticArguments.definition argument
        integer (int64 definition.Minimum) (int64 definition.Maximum)

    let textLengthParameters =
        (record (fun v0 ->
            {
                TextLengthParameters.MaximumCharacters = v0
            })
        : Fields<TextLengthParameters, _>)
        |> field
            (DiagnosticArguments.token DiagnosticArgument.MaximumCharacters)
            _.MaximumCharacters
            (argumentCodec DiagnosticArgument.MaximumCharacters)
        |> build
        |> named "TextLengthParameters"

    let decimalParameters =
        (record (fun v0 v1 ->
            {
                DecimalParameters.MaximumIntegerDigits = v0
                MaximumFractionalDigits = v1
            })
        : Fields<DecimalParameters, _>)
        |> field
            (DiagnosticArguments.token DiagnosticArgument.MaximumIntegerDigits)
            _.MaximumIntegerDigits
            (argumentCodec DiagnosticArgument.MaximumIntegerDigits)
        |> field
            (DiagnosticArguments.token DiagnosticArgument.MaximumFractionalDigits)
            _.MaximumFractionalDigits
            (argumentCodec DiagnosticArgument.MaximumFractionalDigits)
        |> build
        |> named "DecimalParameters"

    let pageParameters =
        (record (fun v0 -> { PageParameters.MaximumPageSize = v0 }): Fields<PageParameters, _>)
        |> field
            (DiagnosticArguments.token DiagnosticArgument.MaximumPageSize)
            _.MaximumPageSize
            (argumentCodec DiagnosticArgument.MaximumPageSize)
        |> build
        |> named "PageParameters"

    let private diagnostics0 =
        [
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.TextRequired)
                Diagnostic.TextRequired
                (function
                | Diagnostic.TextRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.MalformedUnicode)
                Diagnostic.MalformedUnicode
                (function
                | Diagnostic.MalformedUnicode value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.SurroundingWhitespace)
                Diagnostic.SurroundingWhitespace
                (function
                | Diagnostic.SurroundingWhitespace value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.TextTooLong)
                Diagnostic.TextTooLong
                (function
                | Diagnostic.TextTooLong value -> Some value
                | _ -> None)
                (record id |> field "parameters" id textLengthParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.ControlCharacters)
                Diagnostic.ControlCharacters
                (function
                | Diagnostic.ControlCharacters value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.FormattingCharacter)
                Diagnostic.FormattingCharacter
                (function
                | Diagnostic.FormattingCharacter value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics1 =
        [
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.ReferenceFormat)
                Diagnostic.ReferenceFormat
                (function
                | Diagnostic.ReferenceFormat value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.CountryFormat)
                Diagnostic.CountryFormat
                (function
                | Diagnostic.CountryFormat value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.CalendarDateRequired)
                Diagnostic.CalendarDateRequired
                (function
                | Diagnostic.CalendarDateRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.ChronologicalOrder)
                Diagnostic.ChronologicalOrder
                (function
                | Diagnostic.ChronologicalOrder value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.FutureDate)
                Diagnostic.FutureDate
                (function
                | Diagnostic.FutureDate value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.DecimalFormat)
                Diagnostic.DecimalFormat
                (function
                | Diagnostic.DecimalFormat value -> Some value
                | _ -> None)
                (record id |> field "parameters" id decimalParameters)
        ]

    let private diagnostics2 =
        [
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.CurrencyFormat)
                Diagnostic.CurrencyFormat
                (function
                | Diagnostic.CurrencyFormat value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.EmptyOperationId)
                Diagnostic.EmptyOperationId
                (function
                | Diagnostic.EmptyOperationId value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.ExpectedRevisionOutOfRange)
                Diagnostic.ExpectedRevisionOutOfRange
                (function
                | Diagnostic.ExpectedRevisionOutOfRange value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.RevisionExhausted)
                Diagnostic.RevisionExhausted
                (function
                | Diagnostic.RevisionExhausted value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.PaymentClearRequired)
                Diagnostic.PaymentClearRequired
                (function
                | Diagnostic.PaymentClearRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.PaymentAcknowledgementRequired)
                Diagnostic.PaymentAcknowledgementRequired
                (function
                | Diagnostic.PaymentAcknowledgementRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics3 =
        [
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.CaseAlreadyExists)
                Diagnostic.CaseAlreadyExists
                (function
                | Diagnostic.CaseAlreadyExists value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.RevisionConflict)
                Diagnostic.RevisionConflict
                (function
                | Diagnostic.RevisionConflict value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.CaseClosed)
                Diagnostic.CaseClosed
                (function
                | Diagnostic.CaseClosed value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.AlreadyClosed)
                Diagnostic.AlreadyClosed
                (function
                | Diagnostic.AlreadyClosed value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.AlreadyOpened)
                Diagnostic.AlreadyOpened
                (function
                | Diagnostic.AlreadyOpened value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.AmendmentRequiresUndecided)
                Diagnostic.AmendmentRequiresUndecided
                (function
                | Diagnostic.AmendmentRequiresUndecided value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics4 =
        [
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.CorrectionNoChanges)
                Diagnostic.CorrectionNoChanges
                (function
                | Diagnostic.CorrectionNoChanges value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.CorrectionRequiresExistingValue)
                Diagnostic.CorrectionRequiresExistingValue
                (function
                | Diagnostic.CorrectionRequiresExistingValue value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.DecisionRequired)
                Diagnostic.DecisionRequired
                (function
                | Diagnostic.DecisionRequired value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.PaymentAlreadyRecorded)
                Diagnostic.PaymentAlreadyRecorded
                (function
                | Diagnostic.PaymentAlreadyRecorded value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.PaymentNotRecorded)
                Diagnostic.PaymentNotRecorded
                (function
                | Diagnostic.PaymentNotRecorded value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.DecisionAlreadyPaid)
                Diagnostic.DecisionAlreadyPaid
                (function
                | Diagnostic.DecisionAlreadyPaid value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics5 =
        [
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.ZeroDecisionCannotBePaid)
                Diagnostic.ZeroDecisionCannotBePaid
                (function
                | Diagnostic.ZeroDecisionCannotBePaid value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.PageLimitOutOfRange)
                Diagnostic.PageLimitOutOfRange
                (function
                | Diagnostic.PageLimitOutOfRange value -> Some value
                | _ -> None)
                (record id |> field "parameters" id pageParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.InvalidCursor)
                Diagnostic.InvalidCursor
                (function
                | Diagnostic.InvalidCursor value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.ActNotPermitted)
                Diagnostic.ActNotPermitted
                (function
                | Diagnostic.ActNotPermitted value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.LedgerExhausted)
                Diagnostic.LedgerExhausted
                (function
                | Diagnostic.LedgerExhausted value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.IdempotencyConflict)
                Diagnostic.IdempotencyConflict
                (function
                | Diagnostic.IdempotencyConflict value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics6 =
        [
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.ResourceUnavailable)
                Diagnostic.ResourceUnavailable
                (function
                | Diagnostic.ResourceUnavailable value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.CaseVoided)
                Diagnostic.CaseVoided
                (function
                | Diagnostic.CaseVoided value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.CaseErased)
                Diagnostic.CaseErased
                (function
                | Diagnostic.CaseErased value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.AlreadyVoided)
                Diagnostic.AlreadyVoided
                (function
                | Diagnostic.AlreadyVoided value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.NotVoided)
                Diagnostic.NotVoided
                (function
                | Diagnostic.NotVoided value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.LastOwner)
                Diagnostic.LastOwner
                (function
                | Diagnostic.LastOwner value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
        ]

    let private diagnostics7 =
        [
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.ActorAlreadyRegistered)
                Diagnostic.ActorAlreadyRegistered
                (function
                | Diagnostic.ActorAlreadyRegistered value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.ActorUnknown)
                Diagnostic.ActorUnknown
                (function
                | Diagnostic.ActorUnknown value -> Some value
                | _ -> None)
                (record id |> field "parameters" id emptyParameters)
            variant
                (RejectionDiagnosticIds.token RejectionDiagnosticId.HumanPrincipalRequired)
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
            (fun (value: Fault) -> if value.Reason = reason then Some value else None)
            (fields |> build)

    let fault =
        choice
            "Fault"
            [
                faultVariant
                    FaultReason.InternalError
                    FaultDiagnosticIds.InternalError
                    "STOP_AND_INVESTIGATE"
                faultVariant
                    FaultReason.StoreUnavailable
                    FaultDiagnosticIds.StoreUnavailable
                    "RETRY_SAFE"
                faultVariant
                    FaultReason.StoreIntegrityError
                    FaultDiagnosticIds.StoreIntegrityError
                    "STOP_AND_INVESTIGATE"
                faultVariant
                    FaultReason.SchemaMismatch
                    FaultDiagnosticIds.SchemaMismatch
                    "STOP_AND_INVESTIGATE"
                faultVariant
                    FaultReason.OperationContentConflict
                    FaultDiagnosticIds.OperationContentConflict
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

    let private liveSummaryFields =
        record (fun reference revision status disposition ->
            reference, revision, status, disposition)
        |> field "caseReference" (fun (reference, _, _, _) -> reference) reference
        |> field "revision" (fun (_, revision, _, _) -> revision) sequence
        |> field
            "status"
            (fun (_, _, status, _) -> status)
            (enumeration (
                CaseStatuses.all |> List.map (fun status -> status, CaseStatuses.token status)
            ))
        |> field
            "disposition"
            (fun (_, _, _, disposition) -> disposition)
            (enumeration [ Disposition.Active, "ACTIVE"; Disposition.Voided, "VOIDED" ])

    let caseSummary =
        tagged
            "CaseSummary"
            "state"
            [
                variant
                    "LIVE"
                    CaseSummary.Live
                    (function
                    | CaseSummary.Live(reference, revision, status, disposition) ->
                        Some(reference, revision, status, disposition)
                    | _ -> None)
                    liveSummaryFields
                variant
                    "ERASED"
                    CaseSummary.Erased
                    (function
                    | CaseSummary.Erased(reference, revision) -> Some(reference, revision)
                    | _ -> None)
                    (record (fun reference revision -> reference, revision)
                     |> field "caseReference" fst reference
                     |> field "revision" snd sequence)
            ]

    let casesPage =
        (record (fun items next offers ->
            {
                Items = items
                NextCursor = next
                Offers = offers
            })
        : Fields<CaseSummaryPage, _>)
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
