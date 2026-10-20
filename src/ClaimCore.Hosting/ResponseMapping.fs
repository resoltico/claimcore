namespace ClaimCore.Hosting

open ClaimCore.Catalog

open System.Globalization
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting.Presentation

module internal ResponseMapping =
    let private date (value: System.DateOnly) =
        value.ToString("O", CultureInfo.InvariantCulture)

    let private disposition =
        function
        | Disposition.Active -> "ACTIVE"
        | Disposition.Voided -> "VOIDED"

    let private fields (value: CaseFields) : ClaimCore.Wire.CaseFields =
        {
            IncidentDate = value.IncidentDate
            IncidentNotificationDate = value.IncidentNotificationDate
            IncidentCountry = value.IncidentCountry
            ClaimantName = value.ClaimantName
            InsurerName = value.InsurerName
            ClaimedAmount = value.ClaimedAmount
            ClaimedCurrency = value.ClaimedCurrency
            CaseReference = value.CaseReference
            PaymentDecisionDate = value.PaymentDecisionDate
            PayableAmount = value.PayableAmount
            PayableCurrency = value.PayableCurrency
            PaymentDate = value.PaymentDate
            Status = CaseStatuses.token value.Status
        }

    let private live fieldsValue disp : ClaimCore.Wire.LiveState =
        {
            Disposition = disposition disp
            Fields = fields fieldsValue
        }

    let state =
        function
        | CaseView.Erased -> ClaimCore.Wire.CaseState.Erased
        | CaseView.Live(fields, disp) -> ClaimCore.Wire.CaseState.Live(live fields disp)

    let snapshot (value: CaseSnapshot) : ClaimCore.Wire.CaseSnapshot =
        {
            CaseReference = value.Reference
            Revision = value.Revision
            State = state value.View
        }

    let private mode =
        function
        | CorrectionGroupAction.Keep -> "KEEP"
        | CorrectionGroupAction.Replace -> "REPLACE"
        | CorrectionGroupAction.Clear -> "CLEAR"

    let private actOffer =
        function
        | CaseActOffer.Act(act, revision) ->
            ClaimCore.Wire.CaseActOffer.Act(CaseActKinds.token act, revision)
        | CaseActOffer.CorrectCase(revision, choices) ->
            ClaimCore.Wire.CaseActOffer.Correct(
                revision,
                {
                    Registration = List.map mode choices.Registration
                    Decision = List.map mode choices.Decision
                    Payment = List.map mode choices.Payment
                }
            )

    let private historyDetail =
        function
        | HistoryDetail.Summary -> "SUMMARY"
        | HistoryDetail.Full -> "FULL"

    let caseRecord (value: CaseReadModel) : ClaimCore.Wire.CaseView =
        {
            CaseReference = value.Snapshot.Reference
            Revision = value.Snapshot.Revision
            State = state value.Snapshot.View
            Advice =
                {
                    Acts = List.map actOffer value.Advice.Acts
                    History = List.map historyDetail value.Advice.History
                }
        }

    let private offer =
        function
        | InstallationOffer.Open -> ClaimCore.Wire.InstallationOffer.Open 0L
        | InstallationOffer.AuthorityAct act ->
            ClaimCore.Wire.InstallationOffer.AuthorityAct(AuthorityKinds.token act)

    let private view =
        function
        | View.Cases -> "CASES"
        | View.Authority -> "AUTHORITY"
        | View.Operations -> "OPERATIONS"

    let context (value: ActorContext) : ClaimCore.Wire.ActorContext =
        {
            ProductVersion = value.Runtime.ProductVersion
            BusinessDate = date value.Runtime.EffectiveBusinessDate
            TimeZone = value.Runtime.TimeZoneId
            Capabilities = List.map Capabilities.token value.Capabilities
            Views = List.map view value.Views
            Offers = List.map offer value.Offers
        }

    let private runtime (value: RuntimeContext) : ClaimCore.Wire.RuntimeContext =
        {
            ProductVersion = value.ProductVersion
            BusinessDate = date value.EffectiveBusinessDate
            TimeZone = value.TimeZoneId
        }

    let private position (value: LedgerPosition) : ClaimCore.Wire.Position =
        {
            Sequence = value.Sequence
            EntryHash = value.EntryHash
        }

    let private disclosure =
        function
        | SnapshotDisclosure.Withheld -> ClaimCore.Wire.Disclosure.Withheld
        | SnapshotDisclosure.Erased -> ClaimCore.Wire.Disclosure.Erased
        | SnapshotDisclosure.Disclosed(CaseView.Live(fields, disp)) ->
            ClaimCore.Wire.Disclosure.Disclosed(live fields disp)
        | SnapshotDisclosure.Disclosed CaseView.Erased ->
            invalidArg "snapshot" "Erasure has its own disclosure."

    let caseReceipt (value: OperationReceipt) : ClaimCore.Wire.CaseReceipt =
        match value.Scope with
        | ReceiptScope.Case target ->
            {
                OperationId = value.OperationId
                Position = position value.Position
                RecordedAt = value.RecordedAt
                RecordedBy = value.RecordedBy
                CaseReference = target.Reference
                Revision = target.Revision
                Act = CaseActKinds.token target.Act
                Snapshot = disclosure target.Snapshot
            }
        | _ -> invalidArg "receipt" "Expected a case receipt."

    let authorityReceipt (value: OperationReceipt) : ClaimCore.Wire.AuthorityReceipt =
        match value.Scope with
        | ReceiptScope.Authority target ->
            {
                OperationId = value.OperationId
                Position = position value.Position
                RecordedAt = value.RecordedAt
                RecordedBy = value.RecordedBy
                TargetActorId = target.TargetActorId
                Act = AuthorityKinds.token target.Act
            }
        | _ -> invalidArg "receipt" "Expected an authority receipt."

    let receipt (value: OperationReceipt) =
        match value.Scope with
        | ReceiptScope.Case _ -> ClaimCore.Wire.Receipt.Case(caseReceipt value)
        | ReceiptScope.Authority _ -> ClaimCore.Wire.Receipt.Authority(authorityReceipt value)

    let private summary =
        function
        | CaseSummary.Live(reference, revision, status, disp) ->
            ClaimCore.Wire.CaseSummary.Live
                {
                    CaseReference = reference
                    Revision = revision
                    Status = CaseStatuses.token status
                    Disposition = disposition disp
                }
        | CaseSummary.Erased(reference, revision) ->
            ClaimCore.Wire.CaseSummary.Erased
                {
                    CaseReference = reference
                    Revision = revision
                }

    let casesPage (value: CaseSummaryPage) : ClaimCore.Wire.CasesPage =
        {
            Items = List.map summary value.Items
            NextCursor = value.NextCursor
            Offers = List.map offer value.Offers
        }

    let private change (value: ChangeSummary) : ClaimCore.Wire.ChangeSummary =
        {
            OperationId = value.OperationId
            Position = position value.Position
            Revision = value.Revision
            Act = CaseActKinds.token value.Act
            RecordedAt = value.RecordedAt
            RecordedBy = value.RecordedBy
        }

    let private summaryEntry =
        function
        | HistoryEntry.SummaryEntry value -> change value
        | _ -> invalidArg "entries" "History details must be homogeneous."

    let private fullEntry =
        function
        | HistoryEntry.FullEntry value -> caseReceipt value
        | _ -> invalidArg "entries" "History details must be homogeneous."

    let historyPage (value: HistoryResultPage) : ClaimCore.Wire.HistoryPage =
        let entries =
            match value.Entries with
            | [] -> ClaimCore.Wire.HistoryEntries.Empty
            | HistoryEntry.SummaryEntry _ :: _ ->
                ClaimCore.Wire.HistoryEntries.Summary(List.map summaryEntry value.Entries)
            | HistoryEntry.FullEntry _ :: _ ->
                ClaimCore.Wire.HistoryEntries.Full(List.map fullEntry value.Entries)

        {
            Entries = entries
            NextCursor = value.NextCursor
        }

    let private principal value : ClaimCore.Wire.Principal =
        let parts = PrincipalKey.description value

        {
            Kind = parts.Kind
            Issuer = parts.Issuer
            Subject = parts.Subject
        }

    let private actor (value: ActorSummary) : ClaimCore.Wire.ActorSummary =
        {
            ActorId = value.ActorId
            Principal = principal value.Principal
            Enabled = value.Enabled
        }

    let private grant (value: AuthorityGrant) : ClaimCore.Wire.AuthorityGrant =
        {
            Role = RoleTokens.token value.Role
            Scope =
                match value.Scope with
                | GrantTarget.Installation -> ClaimCore.Wire.Scope.Installation
                | GrantTarget.CaseReference reference -> ClaimCore.Wire.Scope.Case reference
        }

    let actorsPage (value: ActorReviewPage) : ClaimCore.Wire.ActorsPage =
        {
            Items = List.map actor value.Items
            NextCursor = value.NextCursor
        }

    let grantsPage (value: AuthorityGrantPage) : ClaimCore.Wire.GrantsPage =
        {
            Actor = actor value.Actor
            Items = List.map grant value.Items
            NextCursor = value.NextCursor
        }

    let review (value: AdvisoryReview) : ClaimCore.Wire.Review =
        {
            Before = Option.map snapshot value.Before
            After = snapshot value.Proposed
            Changes =
                value.Changes
                |> List.map (fun change ->
                    {
                        ClaimCore.Wire.FieldChange.Field = BusinessField.token change.FieldName
                        Before = change.Before
                        After = change.After
                    })
            Context = runtime value.Context
        }

    let private diagnostics =
        [
            RejectionDiagnosticId.TextRequired, ClaimCore.Wire.Diagnostic.TextRequired()
            RejectionDiagnosticId.MalformedUnicode, ClaimCore.Wire.Diagnostic.MalformedUnicode()
            RejectionDiagnosticId.SurroundingWhitespace,
            ClaimCore.Wire.Diagnostic.SurroundingWhitespace()
            RejectionDiagnosticId.ControlCharacters, ClaimCore.Wire.Diagnostic.ControlCharacters()
            RejectionDiagnosticId.CalendarDateRequired,
            ClaimCore.Wire.Diagnostic.CalendarDateRequired()
            RejectionDiagnosticId.ChronologicalOrder, ClaimCore.Wire.Diagnostic.ChronologicalOrder()
            RejectionDiagnosticId.FutureDate, ClaimCore.Wire.Diagnostic.FutureDate()
            RejectionDiagnosticId.CurrencyFormat, ClaimCore.Wire.Diagnostic.CurrencyFormat()
            RejectionDiagnosticId.EmptyOperationId, ClaimCore.Wire.Diagnostic.EmptyOperationId()
            RejectionDiagnosticId.ExpectedRevisionOutOfRange,
            ClaimCore.Wire.Diagnostic.ExpectedRevisionOutOfRange()
            RejectionDiagnosticId.RevisionExhausted, ClaimCore.Wire.Diagnostic.RevisionExhausted()
            RejectionDiagnosticId.PaymentClearRequired,
            ClaimCore.Wire.Diagnostic.PaymentClearRequired()
            RejectionDiagnosticId.PaymentAcknowledgementRequired,
            ClaimCore.Wire.Diagnostic.PaymentAcknowledgementRequired()
            RejectionDiagnosticId.CaseAlreadyExists, ClaimCore.Wire.Diagnostic.CaseAlreadyExists()
            RejectionDiagnosticId.RevisionConflict, ClaimCore.Wire.Diagnostic.RevisionConflict()
            RejectionDiagnosticId.CaseClosed, ClaimCore.Wire.Diagnostic.CaseClosed()
            RejectionDiagnosticId.AlreadyClosed, ClaimCore.Wire.Diagnostic.AlreadyClosed()
            RejectionDiagnosticId.AlreadyOpened, ClaimCore.Wire.Diagnostic.AlreadyOpened()
            RejectionDiagnosticId.AmendmentRequiresUndecided,
            ClaimCore.Wire.Diagnostic.AmendmentRequiresUndecided()
            RejectionDiagnosticId.CorrectionNoChanges,
            ClaimCore.Wire.Diagnostic.CorrectionNoChanges()
            RejectionDiagnosticId.CorrectionRequiresExistingValue,
            ClaimCore.Wire.Diagnostic.CorrectionRequiresExistingValue()
            RejectionDiagnosticId.DecisionRequired, ClaimCore.Wire.Diagnostic.DecisionRequired()
            RejectionDiagnosticId.PaymentAlreadyRecorded,
            ClaimCore.Wire.Diagnostic.PaymentAlreadyRecorded()
            RejectionDiagnosticId.PaymentNotRecorded, ClaimCore.Wire.Diagnostic.PaymentNotRecorded()
            RejectionDiagnosticId.DecisionAlreadyPaid,
            ClaimCore.Wire.Diagnostic.DecisionAlreadyPaid()
            RejectionDiagnosticId.ZeroDecisionCannotBePaid,
            ClaimCore.Wire.Diagnostic.ZeroDecisionCannotBePaid()
            RejectionDiagnosticId.InvalidCursor, ClaimCore.Wire.Diagnostic.InvalidCursor()
            RejectionDiagnosticId.IdempotencyConflict,
            ClaimCore.Wire.Diagnostic.IdempotencyConflict()
            RejectionDiagnosticId.ResourceUnavailable,
            ClaimCore.Wire.Diagnostic.ResourceUnavailable()
            RejectionDiagnosticId.CaseVoided, ClaimCore.Wire.Diagnostic.CaseVoided()
            RejectionDiagnosticId.CaseErased, ClaimCore.Wire.Diagnostic.CaseErased()
            RejectionDiagnosticId.AlreadyVoided, ClaimCore.Wire.Diagnostic.AlreadyVoided()
            RejectionDiagnosticId.NotVoided, ClaimCore.Wire.Diagnostic.NotVoided()
            RejectionDiagnosticId.LastOwner, ClaimCore.Wire.Diagnostic.LastOwner()
            RejectionDiagnosticId.ActorAlreadyRegistered,
            ClaimCore.Wire.Diagnostic.ActorAlreadyRegistered()
            RejectionDiagnosticId.ActorUnknown, ClaimCore.Wire.Diagnostic.ActorUnknown()
            RejectionDiagnosticId.HumanPrincipalRequired,
            ClaimCore.Wire.Diagnostic.HumanPrincipalRequired()
            RejectionDiagnosticId.FormattingCharacter,
            ClaimCore.Wire.Diagnostic.FormattingCharacter()
            RejectionDiagnosticId.ReferenceFormat, ClaimCore.Wire.Diagnostic.ReferenceFormat()
            RejectionDiagnosticId.CountryFormat, ClaimCore.Wire.Diagnostic.CountryFormat()
            RejectionDiagnosticId.ActNotPermitted, ClaimCore.Wire.Diagnostic.ActNotPermitted()
            RejectionDiagnosticId.LedgerExhausted, ClaimCore.Wire.Diagnostic.LedgerExhausted()
        ]
        |> Map.ofList

    let private diagnostic (value: RejectionDiagnostic) =
        match RejectionDiagnostics.parameters value with
        | DiagnosticParameters.MaximumCharacters maximum ->
            ClaimCore.Wire.Diagnostic.TextTooLong { MaximumCharacters = int64 maximum }
        | DiagnosticParameters.DecimalDigits(integer, fraction) ->
            ClaimCore.Wire.Diagnostic.DecimalFormat
                {
                    MaximumIntegerDigits = int64 integer
                    MaximumFractionalDigits = int64 fraction
                }
        | DiagnosticParameters.MaximumPageSize maximum ->
            ClaimCore.Wire.Diagnostic.PageLimitOutOfRange { MaximumPageSize = int64 maximum }
        | DiagnosticParameters.None -> Map.find (RejectionDiagnostics.identifier value) diagnostics

    let private action =
        function
        | RecommendedAction.CorrectInput -> "CORRECT_INPUT"
        | RecommendedAction.ReadCurrent -> "READ_CURRENT"
        | RecommendedAction.RetrySafe -> "RETRY_SAFE"
        | RecommendedAction.ReplayExact -> "REPLAY_EXACT"
        | RecommendedAction.Reauthenticate -> "REAUTHENTICATE"
        | RecommendedAction.StopAndInvestigate -> "STOP_AND_INVESTIGATE"
        | RecommendedAction.NoneRequired -> "NONE_REQUIRED"

    let rejection (value: Rejection) : ClaimCore.Wire.Rejection =
        {
            Diagnostic = diagnostic (RejectionDiagnostics.describe value)
            Message = RejectionPresentation.render value
            Field = value.Field
            ActualRevision = value.ActualRevision
            RecommendedAction = action value.Action
        }

    let fault value : ClaimCore.Wire.Fault =
        let reason =
            match value with
            | CoreFault.InternalError -> ClaimCore.Wire.FaultReason.InternalError
            | CoreFault.StoreUnavailable -> ClaimCore.Wire.FaultReason.StoreUnavailable
            | CoreFault.StoreIntegrityError -> ClaimCore.Wire.FaultReason.StoreIntegrityError
            | CoreFault.SchemaMismatch -> ClaimCore.Wire.FaultReason.SchemaMismatch
            | CoreFault.OperationContentConflict ->
                ClaimCore.Wire.FaultReason.OperationContentConflict

        {
            Reason = reason
            Message = CoreFaultPresentation.render value
        }
