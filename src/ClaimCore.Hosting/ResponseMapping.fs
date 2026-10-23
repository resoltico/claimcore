namespace ClaimCore.Hosting

open ClaimCore.Contract

open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting.Presentation

module internal ResponseMapping =
    let private position (value: LedgerPosition) : ClaimCore.Contract.Protocol.Position =
        {
            Sequence = value.Sequence
            EntryHash = value.EntryHash
        }

    let private disclosure =
        function
        | SnapshotDisclosure.Withheld -> ClaimCore.Contract.Protocol.Disclosure.Withheld
        | SnapshotDisclosure.Erased -> ClaimCore.Contract.Protocol.Disclosure.Erased
        | SnapshotDisclosure.Disclosed(CaseView.Live(fields, disp)) ->
            ClaimCore.Contract.Protocol.Disclosure.Disclosed(CaseView.Live(fields, disp))
        | SnapshotDisclosure.Disclosed CaseView.Erased ->
            invalidArg "snapshot" "Erasure has its own disclosure."

    let caseReceipt (value: OperationReceipt) : ClaimCore.Contract.Protocol.CaseReceipt =
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

    let authorityReceipt (value: OperationReceipt) : ClaimCore.Contract.Protocol.AuthorityReceipt =
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
        | ReceiptScope.Case _ -> ClaimCore.Contract.Protocol.Receipt.Case(caseReceipt value)
        | ReceiptScope.Authority _ ->
            ClaimCore.Contract.Protocol.Receipt.Authority(authorityReceipt value)

    let private change (value: ChangeSummary) : ClaimCore.Contract.Protocol.ChangeSummary =
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

    let historyPage (value: HistoryResultPage) : ClaimCore.Contract.Protocol.HistoryPage =
        let entries =
            match value.Entries with
            | [] -> ClaimCore.Contract.Protocol.HistoryEntries.Empty
            | HistoryEntry.SummaryEntry _ :: _ ->
                ClaimCore.Contract.Protocol.HistoryEntries.Summary(
                    List.map summaryEntry value.Entries
                )
            | HistoryEntry.FullEntry _ :: _ ->
                ClaimCore.Contract.Protocol.HistoryEntries.Full(List.map fullEntry value.Entries)

        {
            Entries = entries
            NextCursor = value.NextCursor
        }

    let private actor (value: ActorSummary) : ClaimCore.Contract.Protocol.ActorSummary =
        {
            ActorId = value.ActorId
            Principal = PrincipalKey.describe value.Principal
            Enabled = value.Enabled
        }

    let private grant (value: AuthorityGrant) : ClaimCore.Contract.Protocol.AuthorityGrant =
        {
            Role = RoleTokens.token value.Role
            Scope = value.Scope
        }

    let actorsPage (value: ActorReviewPage) : ClaimCore.Contract.Protocol.ActorsPage =
        {
            Items = List.map actor value.Items
            NextCursor = value.NextCursor
        }

    let grantsPage (value: AuthorityGrantPage) : ClaimCore.Contract.Protocol.GrantsPage =
        {
            Actor = actor value.Actor
            Items = List.map grant value.Items
            NextCursor = value.NextCursor
        }

    let private diagnostics =
        [
            RejectionDiagnosticId.TextRequired,
            ClaimCore.Contract.Protocol.Diagnostic.TextRequired()
            RejectionDiagnosticId.MalformedUnicode,
            ClaimCore.Contract.Protocol.Diagnostic.MalformedUnicode()
            RejectionDiagnosticId.SurroundingWhitespace,
            ClaimCore.Contract.Protocol.Diagnostic.SurroundingWhitespace()
            RejectionDiagnosticId.ControlCharacters,
            ClaimCore.Contract.Protocol.Diagnostic.ControlCharacters()
            RejectionDiagnosticId.CalendarDateRequired,
            ClaimCore.Contract.Protocol.Diagnostic.CalendarDateRequired()
            RejectionDiagnosticId.ChronologicalOrder,
            ClaimCore.Contract.Protocol.Diagnostic.ChronologicalOrder()
            RejectionDiagnosticId.FutureDate, ClaimCore.Contract.Protocol.Diagnostic.FutureDate()
            RejectionDiagnosticId.CurrencyFormat,
            ClaimCore.Contract.Protocol.Diagnostic.CurrencyFormat()
            RejectionDiagnosticId.EmptyOperationId,
            ClaimCore.Contract.Protocol.Diagnostic.EmptyOperationId()
            RejectionDiagnosticId.ExpectedRevisionOutOfRange,
            ClaimCore.Contract.Protocol.Diagnostic.ExpectedRevisionOutOfRange()
            RejectionDiagnosticId.RevisionExhausted,
            ClaimCore.Contract.Protocol.Diagnostic.RevisionExhausted()
            RejectionDiagnosticId.PaymentClearRequired,
            ClaimCore.Contract.Protocol.Diagnostic.PaymentClearRequired()
            RejectionDiagnosticId.PaymentAcknowledgementRequired,
            ClaimCore.Contract.Protocol.Diagnostic.PaymentAcknowledgementRequired()
            RejectionDiagnosticId.CaseAlreadyExists,
            ClaimCore.Contract.Protocol.Diagnostic.CaseAlreadyExists()
            RejectionDiagnosticId.RevisionConflict,
            ClaimCore.Contract.Protocol.Diagnostic.RevisionConflict()
            RejectionDiagnosticId.CaseClosed, ClaimCore.Contract.Protocol.Diagnostic.CaseClosed()
            RejectionDiagnosticId.AlreadyClosed,
            ClaimCore.Contract.Protocol.Diagnostic.AlreadyClosed()
            RejectionDiagnosticId.AlreadyOpened,
            ClaimCore.Contract.Protocol.Diagnostic.AlreadyOpened()
            RejectionDiagnosticId.AmendmentRequiresUndecided,
            ClaimCore.Contract.Protocol.Diagnostic.AmendmentRequiresUndecided()
            RejectionDiagnosticId.CorrectionNoChanges,
            ClaimCore.Contract.Protocol.Diagnostic.CorrectionNoChanges()
            RejectionDiagnosticId.CorrectionRequiresExistingValue,
            ClaimCore.Contract.Protocol.Diagnostic.CorrectionRequiresExistingValue()
            RejectionDiagnosticId.DecisionRequired,
            ClaimCore.Contract.Protocol.Diagnostic.DecisionRequired()
            RejectionDiagnosticId.PaymentAlreadyRecorded,
            ClaimCore.Contract.Protocol.Diagnostic.PaymentAlreadyRecorded()
            RejectionDiagnosticId.PaymentNotRecorded,
            ClaimCore.Contract.Protocol.Diagnostic.PaymentNotRecorded()
            RejectionDiagnosticId.DecisionAlreadyPaid,
            ClaimCore.Contract.Protocol.Diagnostic.DecisionAlreadyPaid()
            RejectionDiagnosticId.ZeroDecisionCannotBePaid,
            ClaimCore.Contract.Protocol.Diagnostic.ZeroDecisionCannotBePaid()
            RejectionDiagnosticId.InvalidCursor,
            ClaimCore.Contract.Protocol.Diagnostic.InvalidCursor()
            RejectionDiagnosticId.IdempotencyConflict,
            ClaimCore.Contract.Protocol.Diagnostic.IdempotencyConflict()
            RejectionDiagnosticId.ResourceUnavailable,
            ClaimCore.Contract.Protocol.Diagnostic.ResourceUnavailable()
            RejectionDiagnosticId.CaseVoided, ClaimCore.Contract.Protocol.Diagnostic.CaseVoided()
            RejectionDiagnosticId.CaseErased, ClaimCore.Contract.Protocol.Diagnostic.CaseErased()
            RejectionDiagnosticId.AlreadyVoided,
            ClaimCore.Contract.Protocol.Diagnostic.AlreadyVoided()
            RejectionDiagnosticId.NotVoided, ClaimCore.Contract.Protocol.Diagnostic.NotVoided()
            RejectionDiagnosticId.LastOwner, ClaimCore.Contract.Protocol.Diagnostic.LastOwner()
            RejectionDiagnosticId.ActorAlreadyRegistered,
            ClaimCore.Contract.Protocol.Diagnostic.ActorAlreadyRegistered()
            RejectionDiagnosticId.ActorUnknown,
            ClaimCore.Contract.Protocol.Diagnostic.ActorUnknown()
            RejectionDiagnosticId.HumanPrincipalRequired,
            ClaimCore.Contract.Protocol.Diagnostic.HumanPrincipalRequired()
            RejectionDiagnosticId.FormattingCharacter,
            ClaimCore.Contract.Protocol.Diagnostic.FormattingCharacter()
            RejectionDiagnosticId.ReferenceFormat,
            ClaimCore.Contract.Protocol.Diagnostic.ReferenceFormat()
            RejectionDiagnosticId.CountryFormat,
            ClaimCore.Contract.Protocol.Diagnostic.CountryFormat()
            RejectionDiagnosticId.ActNotPermitted,
            ClaimCore.Contract.Protocol.Diagnostic.ActNotPermitted()
            RejectionDiagnosticId.LedgerExhausted,
            ClaimCore.Contract.Protocol.Diagnostic.LedgerExhausted()
        ]
        |> Map.ofList

    let private diagnostic (value: RejectionDiagnostic) =
        match RejectionDiagnostics.parameters value with
        | DiagnosticParameters.MaximumCharacters maximum ->
            ClaimCore.Contract.Protocol.Diagnostic.TextTooLong { MaximumCharacters = int64 maximum }
        | DiagnosticParameters.DecimalDigits(integer, fraction) ->
            ClaimCore.Contract.Protocol.Diagnostic.DecimalFormat
                {
                    MaximumIntegerDigits = int64 integer
                    MaximumFractionalDigits = int64 fraction
                }
        | DiagnosticParameters.MaximumPageSize maximum ->
            ClaimCore.Contract.Protocol.Diagnostic.PageLimitOutOfRange
                { MaximumPageSize = int64 maximum }
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

    let rejection (value: Rejection) : ClaimCore.Contract.Protocol.Rejection =
        {
            Diagnostic = diagnostic (RejectionDiagnostics.describe value)
            Message = RejectionPresentation.render value
            Field = value.Field
            ActualRevision = value.ActualRevision
            RecommendedAction = action value.Action
        }

    let fault value : ClaimCore.Contract.Protocol.Fault =
        let reason =
            match value with
            | CoreFault.InternalError -> ClaimCore.Contract.Protocol.FaultReason.InternalError
            | CoreFault.StoreUnavailable -> ClaimCore.Contract.Protocol.FaultReason.StoreUnavailable
            | CoreFault.StoreIntegrityError ->
                ClaimCore.Contract.Protocol.FaultReason.StoreIntegrityError
            | CoreFault.SchemaMismatch -> ClaimCore.Contract.Protocol.FaultReason.SchemaMismatch
            | CoreFault.OperationContentConflict ->
                ClaimCore.Contract.Protocol.FaultReason.OperationContentConflict

        {
            Reason = reason
            Message = CoreFaultPresentation.render value
        }
