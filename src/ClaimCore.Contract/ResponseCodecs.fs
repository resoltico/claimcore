namespace ClaimCore.Contract.Protocol

open ClaimCore.Contract
open System
open System.Globalization

module ResponseCodecs =
    open Codec
    open ResponseScalars

    let private principal = RequestCodecs.principal
    let private scope = RequestCodecs.scope

    let private registration =
        (record (fun incident notification country claimant insurer amount currency ->
            {
                Registration.IncidentDate = incident
                IncidentNotificationDate = notification
                IncidentCountry = country
                ClaimantName = claimant
                InsurerName = insurer
                ClaimedAmount = amount
                ClaimedCurrency = currency
            })
        : Fields<Registration, _>)
        |> field "incidentDate" _.IncidentDate date
        |> field "incidentNotificationDate" _.IncidentNotificationDate date
        |> field "incidentCountry" _.IncidentCountry country
        |> field "claimantName" _.ClaimantName nonempty
        |> field "insurerName" _.InsurerName nonempty
        |> field "claimedAmount" _.ClaimedAmount amount
        |> field "claimedCurrency" _.ClaimedCurrency currency
        |> build

    let private decision =
        (record (fun date amount currency ->
            {
                Decision.PaymentDecisionDate = date
                PayableAmount = amount
                PayableCurrency = currency
            })
        : Fields<Decision, _>)
        |> field "paymentDecisionDate" _.PaymentDecisionDate date
        |> field "payableAmount" _.PayableAmount amount
        |> field "payableCurrency" _.PayableCurrency currency
        |> build

    let private payment =
        (record (fun date -> { Payment.PaymentDate = date }): Fields<Payment, _>)
        |> field "paymentDate" _.PaymentDate date
        |> build

    let caseFields =
        (record (fun reference status registration decision payment ->
            {
                CaseFields.CaseReference = reference
                Status = status
                Registration = registration
                Decision = decision
                Payment = payment
            })
        : Fields<CaseFields, _>)
        |> field "caseReference" _.CaseReference reference
        |> field "status" _.Status (enumeration (Vocabulary.entries CaseStatuses.vocabulary))
        |> field "registration" _.Registration registration
        |> field "decision" _.Decision (nullable decision)
        |> field "payment" _.Payment (nullable payment)
        |> build
        |> named "CaseFields"

    let private liveStateFields =
        Codec.record (fun disposition fields -> fields, disposition)
        |> field "disposition" snd (enumeration (Vocabulary.entries Dispositions.vocabulary))
        |> field "fields" fst caseFields

    let private liveTag = "LIVE"

    let private liveVariant =
        variant
            liveTag
            CaseView.Live
            (function
            | CaseView.Live(fields, disposition) -> Some(fields, disposition)
            | _ -> None)
            liveStateFields

    let private stateName = "CaseState"

    // Current reads and the LIVE branch of retained views share the same codec definition.
    let liveState =
        let codec = tagged stateName "tag" [ liveVariant ]

        { codec with
            Schema = Schema.Ref(stateName + "." + liveTag)
            Definitions = Map.remove stateName codec.Definitions
        }

    let caseState =
        tagged stateName "tag" [ liveVariant; unitVariant "ERASED" CaseView.Erased ]

    let caseSnapshot =
        (record (fun reference revision view ->
            {
                Reference = reference
                Revision = revision
                View = view
            })
        : Fields<CaseSnapshot, _>)
        |> field (FieldTable.key BusinessField.CaseReference) _.Reference reference
        |> field "revision" _.Revision sequence
        |> field "state" _.View caseState
        |> build
        |> named "CaseSnapshot"

    let private correctionAction = vocabulary CorrectionActions.vocabulary
    let private historyDetail = vocabulary HistoryDetails.vocabulary

    let private authorityKind =
        Vocabulary.entries AuthorityKinds.vocabulary
        |> List.filter (fun (value, _) -> value <> AuthorityKind.ProvisionOwner)
        |> enumeration

    let private maximumInstallationOffers =
        1
        + (AuthorityKinds.all
           |> List.filter ((<>) AuthorityKind.ProvisionOwner)
           |> List.length)

    let private dateValue =
        date
        |> map
            (fun value ->
                DateOnly.ParseExact(
                    value,
                    Formats.CalendarDateFormat,
                    CultureInfo.InvariantCulture
                ))
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
                (Vocabulary.entries CorrectionActions.vocabulary
                 |> List.filter (fun (value, _) -> value <> CorrectionGroupAction.Clear)
                 |> enumeration))
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
                |> List.filter ((<>) (CaseActKind.Business BusinessActKind.Correct))
                |> List.map (fun act -> act, CaseActKinds.token act)
            ))
        |> field "expectedRevision" snd revision

    let private correctionOfferFields =
        record (fun revision correction -> revision, correction)
        |> fixedField "act" (CaseActKinds.token (CaseActKind.Business BusinessActKind.Correct))
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
        |> field (FieldTable.key BusinessField.CaseReference) _.Snapshot.Reference reference
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
        |> field "views" _.Views (list 3 (vocabulary Views.vocabulary))
        |> field "offers" _.Offers (list maximumInstallationOffers installationOffer)
        |> build
        |> named "ActorContext"

    let position =
        (record (fun sequence hash ->
            {
                Sequence = sequence
                EntryHash = hash
            })
        : Fields<LedgerPosition, _>)
        |> field "sequence" _.Sequence sequence
        |> field "entryHash" _.EntryHash digest
        |> build
        |> named "Position"

    let changeSummary =
        (record (fun id position revision act at by ->
            {
                OperationId = id
                Position = position
                Revision = revision
                Act = act
                RecordedAt = at
                RecordedBy = by
            })
        : Fields<ChangeSummary, _>)
        |> field "operationId" _.OperationId uuid
        |> field "position" _.Position position
        |> field "revision" _.Revision sequence
        |> field
            "act"
            _.Act
            (enumeration (CaseActKinds.all |> List.map (fun act -> act, CaseActKinds.token act)))
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy uuid
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
        |> field
            "role"
            _.Role
            (enumeration (RoleTokens.all |> List.map (fun role -> role, RoleTokens.token role)))
        |> field "scope" _.Scope (scope)
        |> build
        |> named "AuthorityGrant"

    let grantsPage =
        (record (fun v0 v1 v2 ->
            {
                AuthorityGrantPage.Actor = v0
                Items = v1
                NextCursor = v2
            })
        : Fields<AuthorityGrantPage, _>)
        |> field "actor" _.Actor (actorSummary)
        |> field "items" _.Items (list 50 authorityGrant)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "GrantsPage"

    let actorsPage =
        (record (fun v0 v1 ->
            {
                ActorReviewPage.Items = v0
                NextCursor = v1
            })
        : Fields<ActorReviewPage, _>)
        |> field "items" _.Items (list 50 actorSummary)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "ActorsPage"

    let private summaryEntry =
        record id
        |> fixedField "tag" "SUMMARY"
        |> field "change" id changeSummary
        |> build
        |> map HistoryEntry.SummaryEntry (function
            | HistoryEntry.SummaryEntry value -> value
            | _ -> invalidArg "entry" "Expected summary history.")

    let private fullEntry receiptCodec =
        record id
        |> fixedField "tag" "FULL"
        |> field "receipt" id receiptCodec
        |> build
        |> map HistoryEntry.FullEntry (function
            | HistoryEntry.FullEntry value -> value
            | _ -> invalidArg "entry" "Expected full history.")

    let private historyEntriesFor receiptCodec =
        let nonempty predicate entries =
            not (List.isEmpty entries) && List.forall predicate entries

        choice
            "HistoryEntries"
            [
                branch
                    (fun value -> value)
                    (function
                    | [] -> Some []
                    | _ -> None)
                    (boundedList 0 0 summaryEntry)
                branch
                    (fun value -> value)
                    (fun entries ->
                        if
                            nonempty
                                (function
                                | HistoryEntry.SummaryEntry _ -> true
                                | _ -> false)
                                entries
                        then
                            Some entries
                        else
                            None)
                    (boundedList 1 Limits.MaximumPageSize summaryEntry)
                branch
                    (fun value -> value)
                    (fun entries ->
                        if
                            nonempty
                                (function
                                | HistoryEntry.FullEntry _ -> true
                                | _ -> false)
                                entries
                        then
                            Some entries
                        else
                            None)
                    (boundedList 1 Limits.MaximumPageSize (fullEntry receiptCodec))
            ]

    let historyPageFor receiptCodec =
        (record (fun entries next -> { Entries = entries; NextCursor = next })
        : Fields<HistoryResultPage, _>)
        |> field "entries" _.Entries (historyEntriesFor receiptCodec)
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

    let private liveSummaryFields =
        record (fun reference revision status disposition ->
            reference, revision, status, disposition)
        |> field
            (FieldTable.key BusinessField.CaseReference)
            (fun (reference, _, _, _) -> reference)
            reference
        |> field "revision" (fun (_, revision, _, _) -> revision) sequence
        |> field
            (FieldTable.key BusinessField.Status)
            (fun (_, _, status, _) -> status)
            (enumeration (
                CaseStatuses.all |> List.map (fun status -> status, CaseStatuses.token status)
            ))
        |> field
            "disposition"
            (fun (_, _, _, disposition) -> disposition)
            (enumeration (Vocabulary.entries Dispositions.vocabulary))

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
                     |> field (FieldTable.key BusinessField.CaseReference) fst reference
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
        |> field "offers" _.Offers (list maximumInstallationOffers installationOffer)
        |> build
        |> named "CasesPage"
