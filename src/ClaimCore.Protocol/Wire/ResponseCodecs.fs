namespace ClaimCore.Protocol

open ClaimCore.Domain

open ClaimCore.Protocol
open ClaimCore.Json
open System
open System.Globalization

module ResponseCodecs =
    open Codec
    open ResponseScalars

    let private principal = PrincipalCodecs.key

    let private scope = RequestCodecs.scope

    let private scalar kind =
        let codec =
            match kind with
            | FieldValueKind.CalendarDate -> date
            | FieldValueKind.Country -> country
            | FieldValueKind.PartyName -> nonempty
            | FieldValueKind.Amount -> amount
            | FieldValueKind.Currency -> currency
            | FieldValueKind.Reference
            | FieldValueKind.Status -> invalidArg (nameof kind) "A group contains business values."

        codec
        |> mapResult
            (fun path raw ->
                FieldValue.parse kind raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "value outside its wire grammar"
                    }))
            FieldValue.format

    let private group group create values =
        CaseFields.ofGroup group
        |> List.map (fun field ->
            field, BusinessField.key field, scalar (BusinessField.describe field).Kind)
        |> Codec.fieldMap
        |> mapResult
            (fun path fields ->
                create (Map.toList fields)
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> Map.ofList)

    let private registration =
        group RecordGroup.Registration Registration.create Registration.values

    let private decision = group RecordGroup.Decision Decision.create Decision.values
    let private payment = group RecordGroup.Payment Payment.create Payment.values

    let caseFields =
        (record (fun reference status registration decision payment ->
            PaymentProgress.fromGroups
                decision
                (payment |> Option.map (fun (value: Payment) -> (Payment.date (value))))
            |> Result.map (fun progress ->
                {
                    CaseFields.CaseReference = reference
                    Status = status
                    Registration = registration
                    Progress = progress
                }))
        : Fields<CaseFields, _>)
        |> field (BusinessField.key BusinessField.CaseReference) _.CaseReference caseReference
        |> field
            (BusinessField.key BusinessField.Status)
            _.Status
            (VocabularyCodec.create CaseStatus.vocabulary)
        |> field "registration" _.Registration registration
        |> field "decision" _.Decision (nullable decision)
        |> field "payment" _.Payment (nullable payment)
        |> buildResult (fun path () ->
            {
                Path = path + ".payment"
                Reason = "Payment requires a decision."
            })
        |> named "CaseFields"

    let liveState =
        let fields = record id |> field "fields" id caseFields

        tagged
            "LiveCase"
            [
                variant
                    "ACTIVE"
                    LiveCase.Active
                    (function
                    | LiveCase.Active value -> Some value
                    | _ -> None)
                    fields
                variant
                    "VOIDED"
                    LiveCase.Voided
                    (function
                    | LiveCase.Voided value -> Some value
                    | _ -> None)
                    fields
            ]

    let caseState =
        CaseStateCodecs.state "CaseState" (record id |> field "fields" id caseFields)

    let caseSnapshot =
        (record (fun reference revision view ->
            {
                CaseSnapshot.Reference = reference
                Revision = revision
                State = view
            })
        : Fields<CaseSnapshot, _>)
        |> field (BusinessField.key BusinessField.CaseReference) _.Reference caseReference
        |> field "revision" _.Revision positiveRevision
        |> field "state" _.State caseState
        |> build
        |> named "CaseSnapshot"

    let private correctionAction =
        VocabularyCodec.create CorrectionGroupAction.vocabulary

    let private historyDetail = VocabularyCodec.create HistoryDetail.vocabulary

    let private authorityKind =
        VocabularyCodec.select ((<>) AuthorityKind.ProvisionOwner) AuthorityKind.vocabulary

    let private maximumInstallationOffers =
        1
        + (AuthorityKind.all
           |> List.filter ((<>) AuthorityKind.ProvisionOwner)
           |> List.length)

    let private dateValue =
        date
        |> map
            (fun value ->
                DateOnly.ParseExact(value, CalendarDate.ExactFormat, CultureInfo.InvariantCulture))
            (fun value -> value.ToString("O", CultureInfo.InvariantCulture))

    let correctionAdvice =
        (record (fun registration decision payment ->
            {
                CorrectionChoices.Registration = registration
                Decision = decision
                Payment = payment
            })
        : Fields<CorrectionChoices, _>)
        |> field
            "registration"
            _.Registration
            (list
                2
                (VocabularyCodec.select
                    (fun value -> List.contains value Amendment.actions)
                    CorrectionGroupAction.vocabulary))
        |> field "decision" _.Decision (list 3 correctionAction)
        |> field "payment" _.Payment (list 3 correctionAction)
        |> build
        |> named "CorrectionAdvice"

    let caseAdvice =
        record (fun acts correction history -> acts, correction, history)
        |> field
            "acts"
            (fun (acts, _, _) -> acts)
            (list CaseActKind.all.Length (VocabularyCodec.create CaseActKind.vocabulary))
        |> optionalField "correction" (fun (_, correction, _) -> correction) correctionAdvice
        |> field "history" (fun (_, _, history) -> history) (list 2 historyDetail)
        |> build
        |> mapResult
            (fun path (acts, correction, history) ->
                CaseAdvice.create acts correction history
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (fun (advice: CaseAdvice) -> advice.Acts, advice.Correction, advice.History)
        |> named "CaseAdvice"

    let caseView =
        (record (fun reference revision view advice assigned ->
            {
                Snapshot =
                    {
                        Reference = reference
                        Revision = revision
                        State = view
                    }
                Advice = advice
                AssignedToYou = assigned
            })
        : Fields<CaseReadModel, _>)
        |> field (BusinessField.key BusinessField.CaseReference) _.Snapshot.Reference caseReference
        |> field "revision" _.Snapshot.Revision positiveRevision
        |> field "state" _.Snapshot.State caseState
        |> field "advice" _.Advice caseAdvice
        |> field "assignedToYou" _.AssignedToYou boolean
        |> build
        |> named "CaseView"

    let installationOffer =
        tagged
            "InstallationOffer"
            [
                unitVariant "OPEN" InstallationOffer.Open
                variant
                    "AUTHORITY_ACT"
                    InstallationOffer.AuthorityAct
                    (function
                    | InstallationOffer.AuthorityAct act -> Some act
                    | _ -> None)
                    (record id |> field "act" id authorityKind)
            ]

    let private runtimeFields =
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

    let runtimeContext = runtimeFields |> build |> named "RuntimeContext"

    let actorContext =
        (record (fun runtime capabilities views offers assigned ->
            {
                ActorContext.Runtime = runtime
                Capability = capabilities
                View = views
                Offers = offers
                HasAssignedCases = assigned
            })
        : Fields<ActorContext, _>)
        |> flatten _.Runtime runtimeFields
        |> field
            "capabilities"
            _.Capability
            (list 16 (VocabularyCodec.create Capability.vocabulary))
        |> field "views" _.View (list 3 (VocabularyCodec.create View.vocabulary))
        |> field "offers" _.Offers (list maximumInstallationOffers installationOffer)
        |> field "hasAssignedCases" _.HasAssignedCases boolean
        |> build
        |> named "ActorContext"

    let position = LedgerCodecs.position
    let changeSummary = LedgerCodecs.changeSummary
    let fieldChange = LedgerCodecs.fieldChange

    let reviewFields =
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
        |> field "changes" _.Changes (list CaseFields.declarations.Length fieldChange)
        |> field "context" _.Context runtimeContext

    let actorSummary =
        (record (fun v0 v1 v2 ->
            {
                Actor.ActorId = v0
                Principal = v1
                Enabled = v2
            })
        : Fields<Actor, _>)
        |> field "actorId" _.ActorId actorId
        |> field "principal" _.Principal (principal)
        |> field "enabled" _.Enabled (boolean)
        |> build
        |> named "ActorSummary"

    let internal authorityGrantFields =
        (record (fun v0 v1 -> { AuthoredGrant.Role = v0; Scope = v1 }): Fields<AuthoredGrant, _>)
        |> field "role" _.Role (VocabularyCodec.create Role.vocabulary)
        |> field "scope" _.Scope (scope)

    let authorityGrant = authorityGrantFields |> build |> named "AuthorityGrant"

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
        |> fixedField "kind" "SUMMARY"
        |> field "change" id changeSummary
        |> build
        |> map HistoryEntry.SummaryEntry (function
            | HistoryEntry.SummaryEntry value -> value
            | _ -> invalidArg "entry" "Expected summary history.")

    let private fullEntry receiptCodec =
        record id
        |> fixedField "kind" "FULL"
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
                    (boundedList 1 Paging.MaximumPageSize summaryEntry)
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
                    (boundedList 1 Paging.MaximumPageSize (fullEntry receiptCodec))
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

    let caseSummary = ReadRowCodecs.caseSummary

    let casesPage =
        (record (fun items next offers ->
            {
                Items = items
                NextCursor = next
                Offers = offers
            })
        : Fields<CaseSummaryPage, _>)
        |> field "items" _.Items (list 50 caseSummary)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumCaseCursorCharacters))
        |> field "offers" _.Offers (list maximumInstallationOffers installationOffer)
        |> build
        |> named "CasesPage"
