namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts

open ClaimCore.Protocol
open ClaimCore.Json
open System
open System.Globalization

module ResponseCodecs =
    open Codec
    open ResponseScalars

    let private principal = PrincipalCodecs.key

    let grantScope =
        Codec.tagged
            "GrantScope"
            [
                Codec.unitVariant "INSTALLATION" GrantScope.Installation
                Codec.variant
                    "CASE"
                    (fun (id, reference) -> GrantScope.Case(id, reference))
                    (function
                    | GrantScope.Case(id, reference) -> Some(id, reference)
                    | _ -> None)
                    (Codec.record (fun id reference -> id, reference)
                     |> Codec.field "caseId" fst caseId
                     |> Codec.field "caseReference" snd caseReference)
            ]
        |> Codec.vocabulary "scope"

    let private group group create values =
        CaseFields.ofGroup group
        |> List.filter ((<>) BusinessField.IncidentDescription)
        |> List.map (fun field ->
            field,
            BusinessField.key field,
            CaseFieldCodecs.scalar (BusinessField.describe field).Kind)
        |> Codec.fieldMap
        |> mapResult
            (fun path fields ->
                create (Map.toList fields)
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> Map.ofList)

    let private registration =
        (record (fun incident notification country claimant insurer amount currency description ->
            [
                BusinessField.IncidentDate, incident
                BusinessField.IncidentNotificationDate, notification
                BusinessField.IncidentCountry, country
                BusinessField.ClaimantName, claimant
                BusinessField.InsurerName, insurer
                BusinessField.ClaimedAmount, amount
                BusinessField.ClaimedCurrency, currency
                yield!
                    description
                    |> Option.map (fun value -> BusinessField.IncidentDescription, value)
                    |> Option.toList
            ]
            |> Registration.create)
        : Fields<Registration, _>)
        |> field
            "incidentDate"
            (Registration.incidentDate >> Admitted.Date)
            (CaseFieldCodecs.scalar FieldValueKind.CalendarDate)
        |> field
            "incidentNotificationDate"
            (Registration.incidentNotificationDate >> Admitted.Date)
            (CaseFieldCodecs.scalar FieldValueKind.CalendarDate)
        |> field
            "incidentCountry"
            (Registration.country >> Admitted.Country)
            (CaseFieldCodecs.scalar FieldValueKind.Country)
        |> field
            "claimantName"
            (Registration.claimant >> Admitted.Name)
            (CaseFieldCodecs.scalar FieldValueKind.PartyName)
        |> field
            "insurerName"
            (Registration.insurer >> Admitted.Name)
            (CaseFieldCodecs.scalar FieldValueKind.PartyName)
        |> field
            "claimedAmount"
            (Registration.claimed >> Money.amount >> Admitted.Amount)
            (CaseFieldCodecs.scalar FieldValueKind.Amount)
        |> field
            "claimedCurrency"
            (Registration.claimed >> Money.currencyValue >> Admitted.Currency)
            (CaseFieldCodecs.scalar FieldValueKind.Currency)
        |> field
            "incidentDescription"
            (Registration.description >> Option.map Admitted.Description)
            (nullable (CaseFieldCodecs.scalar FieldValueKind.IncidentDescription))
        |> buildResult (fun path reason -> { Path = path; Reason = reason })

    let private decision = group RecordGroup.Decision Decision.create Decision.values
    let private payment = group RecordGroup.Payment Payment.create Payment.values

    let caseFields =
        (record (fun reference status registration decision payment suppliedProgress ->
            PaymentProgress.fromGroups decision payment
            |> Result.bind (fun progress ->
                if CaseProgress.ofPayment progress <> suppliedProgress then
                    Error()
                else
                    Ok
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
        |> field
            "progress"
            (_.Progress >> CaseProgress.ofPayment)
            (VocabularyCodec.create CaseProgress.vocabulary)
        |> buildResult (fun path () ->
            {
                Path = path + ".payment"
                Reason = "Payment groups and supplied progress must agree."
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
        (record (fun id reference revision view ->
            {
                CaseSnapshot.CaseId = id
                Reference = reference
                Revision = revision
                State = view
            })
        : Fields<CaseSnapshot, _>)
        |> field "caseId" _.CaseId caseId
        |> field (BusinessField.key BusinessField.CaseReference) _.Reference caseReference
        |> field "revision" _.Revision positiveRevision
        |> field "state" _.State caseState
        |> build
        |> named "CaseSnapshot"

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
        |> field
            "decision"
            _.Decision
            (list
                Change.actions.Length
                (VocabularyCodec.select
                    (fun value -> List.contains value Change.actions)
                    CorrectionGroupAction.vocabulary))
        |> field
            "payment"
            _.Payment
            (list
                PaymentChange.actions.Length
                (VocabularyCodec.select
                    (fun value -> List.contains value PaymentChange.actions)
                    CorrectionGroupAction.vocabulary))
        |> build
        |> named "CorrectionAdvice"

    let registerMaintenance =
        record (fun active generation -> active, generation)
        |> field "active" fst boolean
        |> field "generation" snd (integer 0L SafeInteger.Maximum)
        |> build
        |> mapResult
            (fun path (active, generation) ->
                RegisterMaintenance.create active generation
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (fun (state: RegisterMaintenance) -> state.Active, state.Generation)
        |> named "RegisterMaintenance"

    let private blockedCaseAct =
        (record (fun act reason ->
            {
                CaseActRefusal.Act = act
                Reason = reason
            })
        : Fields<CaseActRefusal, _>)
        |> field "act" _.Act (VocabularyCodec.create CaseActKind.vocabulary)
        |> field "reason" _.Reason DiagnosticCodecs.caseActAvailability
        |> build
        |> named "CaseActRefusal"

    let caseAdvice =
        record (fun acts correction history maintenance recommended blocked ->
            acts, correction, history, maintenance, recommended, blocked)
        |> field
            "acts"
            (fun (acts, _, _, _, _, _) -> acts)
            (list CaseActKind.all.Length (VocabularyCodec.create CaseActKind.vocabulary))
        |> optionalField
            "correction"
            (fun (_, correction, _, _, _, _) -> correction)
            correctionAdvice
        |> field "history" (fun (_, _, history, _, _, _) -> history) (list 2 historyDetail)
        |> field "maintenance" (fun (_, _, _, maintenance, _, _) -> maintenance) registerMaintenance
        |> field
            "recommended"
            (fun (_, _, _, _, recommended, _) -> recommended)
            (nullable (VocabularyCodec.create CaseActKind.vocabulary))
        |> field
            "blocked"
            (fun (_, _, _, _, _, blocked) -> blocked)
            (list CaseActKind.all.Length blockedCaseAct)
        |> build
        |> mapResult
            (fun path (acts, correction, history, maintenance, recommended, blocked) ->
                CaseAdvice.createDuringMaintenance maintenance acts correction history blocked
                |> Result.bind (CaseAdvice.withRecommendation recommended)
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (fun (advice: CaseAdvice) ->
                advice.Acts,
                advice.Correction,
                advice.History,
                advice.Maintenance,
                advice.Recommended,
                advice.Blocked)
        |> fun codec ->
            { codec with
                Schema =
                    Schema.EnumPartition(
                        codec.Schema,
                        CaseActKind.all |> List.map CaseActKind.token,
                        "acts",
                        "blocked",
                        "act"
                    )
            }
        |> named "CaseAdvice"

    let caseView =
        (record (fun id reference revision view advice assigned work headline attention stages ->
            {
                Snapshot =
                    {
                        CaseId = id
                        Reference = reference
                        Revision = revision
                        State = view
                    }
                Advice = advice
                Mine = assigned
                Work = work
                Headline = headline
                Attention = attention
                Stages = stages
            })
        : Fields<CaseReadModel, _>)
        |> field "caseId" _.Snapshot.CaseId caseId
        |> field (BusinessField.key BusinessField.CaseReference) _.Snapshot.Reference caseReference
        |> field "revision" _.Snapshot.Revision positiveRevision
        |> field "state" _.Snapshot.State caseState
        |> field "advice" _.Advice caseAdvice
        |> field "mine" _.Mine boolean
        |> field "work" _.Work ReadRowCodecs.workView
        |> field "headline" _.Headline (VocabularyCodec.create CaseHeadline.vocabulary)
        |> field
            "attention"
            _.Attention
            (list CaseAttention.all.Length (VocabularyCodec.create CaseAttention.vocabulary))
        |> field "stages" _.Stages CaseStageCodecs.display
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
        (record (fun version date zone observed ->
            {
                RuntimeContext.ProductVersion = version
                Basis =
                    {
                        ObservedAt = observed
                        BusinessDate = date
                        TimeZoneId = zone
                    }
            })
        : Fields<RuntimeContext, _>)
        |> field "productVersion" _.ProductVersion nonempty
        |> field "businessDate" (fun value -> value.Basis.BusinessDate) dateValue
        |> field "timeZone" (fun value -> value.Basis.TimeZoneId) nonempty
        |> field "observedAt" (fun value -> value.Basis.ObservedAt) instant

    let runtimeContext = runtimeFields |> build |> named "RuntimeContext"

    let private contextActor =
        (record (fun identity assigned -> identity, assigned): Fields<ActorId * bool, _>)
        |> field "actorId" fst actorId
        |> field "hasScopedCases" snd boolean

    let private actorContextFields =
        (record
            (fun
                installation
                runtime
                capabilities
                views
                offers
                (identity, assigned)
                maintenance
                self
                issuer ->
                {
                    ActorContext.Issuer = issuer
                    InstallationId = installation
                    Self = self
                    Maintenance = maintenance
                    ActorId = identity
                    Runtime = runtime
                    Capability = capabilities
                    View = views
                    Offers = offers
                    HasScopedCases = assigned
                })
        : Fields<ActorContext, _>)
        |> field "installationId" _.InstallationId uuid
        |> flatten _.Runtime runtimeFields
        |> field
            "capabilities"
            _.Capability
            (list 16 (VocabularyCodec.create Capability.vocabulary))
        |> field "views" _.View (list 3 (VocabularyCodec.create View.vocabulary))
        |> field "offers" _.Offers (list maximumInstallationOffers installationOffer)
        |> flatten (fun context -> context.ActorId, context.HasScopedCases) contextActor
        |> field "maintenance" _.Maintenance registerMaintenance
        |> field "self" _.Self ActorCodecs.actorRef
        |> field "issuer" _.Issuer PrincipalCodecs.issuer

    let actorContext = actorContextFields |> build |> named "ActorContext"

    let requesterClaims =
        let hint =
            partyName
            |> constrained
                (Schema.String(None, None, Some 1, Some 200))
                (PartyName.admit >> Result.isOk)

        (record (fun name username email ->
            RequesterClaims.admit
                (name |> Option.map PartyName.format)
                (username |> Option.map PartyName.format)
                (email |> Option.map PartyName.format))
        : Fields<RequesterClaims, _>)
        |> optionalField "name" _.Name hint
        |> optionalField "preferredUsername" _.PreferredUsername hint
        |> optionalField "email" _.Email hint
        |> build
        |> named "RequesterClaims"

    let private requesterState =
        tagged
            "RequesterState"
            [
                variant
                    (RequesterKind.token RequesterKind.Active)
                    RequesterState.Active
                    (function
                    | RequesterState.Active value -> Some value
                    | _ -> None)
                    actorContextFields
                variant
                    (RequesterKind.token RequesterKind.NoRoles)
                    RequesterState.NoRoles
                    (function
                    | RequesterState.NoRoles value -> Some value
                    | _ -> None)
                    ((record (fun self installation date zone ->
                        {
                            RequesterWithoutRoles.Self = self
                            InstallationId = installation
                            BusinessDate = date
                            TimeZone = zone
                        })
                     : Fields<RequesterWithoutRoles, _>)
                     |> field "self" _.Self ActorCodecs.actorRef
                     |> field "installationId" _.InstallationId uuid
                     |> field "businessDate" _.BusinessDate dateValue
                     |> field "timeZone" _.TimeZone text)
                variant
                    (RequesterKind.token RequesterKind.Disabled)
                    RequesterState.Disabled
                    (function
                    | RequesterState.Disabled value -> Some value
                    | _ -> None)
                    (record id |> field "self" id ActorCodecs.actorRef)
                variant
                    (RequesterKind.token RequesterKind.NotRegistered)
                    RequesterState.NotRegistered
                    (function
                    | RequesterState.NotRegistered value -> Some value
                    | _ -> None)
                    ((record (fun issuer subject claims ->
                        {
                            UnregisteredRequester.Issuer = issuer
                            Subject = subject
                            Claims = claims
                        })
                     : Fields<UnregisteredRequester, _>)
                     |> field "issuer" _.Issuer (boundedText 1 PrincipalKey.MaximumIssuerCharacters)
                     |> field
                         "subject"
                         _.Subject
                         (boundedText 1 PrincipalKey.MaximumHumanSubjectCharacters)
                     |> field "claims" _.Claims requesterClaims)
            ]

    let contextReply =
        record (fun requester -> { ContextReply.Requester = requester })
        |> field "requester" _.Requester requesterState
        |> build
        |> named "RequesterContext"

    let private accessRequest =
        (record (fun principal first last claims ->
            {
                AccessRequest.Principal = principal
                FirstSeen = first
                LastSeen = last
                Claims = claims
            })
        : Fields<AccessRequest, _>)
        |> field "principal" _.Principal principal
        |> field "firstSeen" _.FirstSeen instant
        |> field "lastSeen" _.LastSeen instant
        |> field "claims" _.Claims requesterClaims
        |> build
        |> named "AccessRequest"

    let accessRequestsPage =
        (record (fun items cursor ->
            {
                AccessRequestPage.Items = items
                NextCursor = cursor
            })
        : Fields<AccessRequestPage, _>)
        |> field "items" _.Items (list Paging.MaximumPageSize accessRequest)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumAccessRequestCursorCharacters))
        |> build
        |> named "AccessRequestPage"

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

    let private actorResponsibleCases =
        (record (fun count actor _view ->
            {
                ActorResponsibleCases.Count = count
                ResponsibleActor = actor
            })
        : Fields<ActorResponsibleCases, _>)
        |> field "count" _.Count (integer 0L SafeInteger.Maximum)
        |> field "responsibleActor" _.ResponsibleActor actorId
        |> field "view" _.View (constant "ALL" |> map (fun () -> CaseView.All) (fun _ -> ()))
        |> build
        |> named "ActorResponsibleCases"

    let private actorRoleSummary =
        (record (fun role installation count ->
            {
                ActorRoleSummary.Role = role
                Installation = installation
                CaseCount = count
            })
        : Fields<ActorRoleSummary, _>)
        |> field "role" _.Role (VocabularyCodec.create Role.vocabulary)
        |> field "installation" _.Installation boolean
        |> field "caseCount" _.CaseCount (integer 0L SafeInteger.Maximum)
        |> build
        |> named "ActorRoleSummary"

    let actorSummary =
        let contact =
            nonempty
            |> mapResult
                (fun path raw ->
                    ContactText.parse raw
                    |> Result.mapError (fun _ ->
                        {
                            Path = path
                            Reason = "Invalid profile contact."
                        }))
                ContactText.format

        (record (fun actor principal contact responsibleCases state roles ->
            {
                ActorReviewItem.State = state
                Roles = roles
                Actor = actor
                Principal = principal
                Contact = contact
                ResponsibleCases = responsibleCases
            })
        : Fields<ActorReviewItem, _>)
        |> field "actor" _.Actor ActorCodecs.actorRef
        |> field "principal" _.Principal principal
        |> field "contact" _.Contact (nullable contact)
        |> optionalField "responsibleCases" _.ResponsibleCases actorResponsibleCases
        |> field "state" _.State (VocabularyCodec.create AccountState.vocabulary)
        |> field "roles" _.Roles (list Role.all.Length actorRoleSummary)
        |> build
        |> named "ActorReviewItem"

    let internal authorityGrantFields =
        (record (fun v0 v1 -> { ScopedGrant.Role = v0; Scope = v1 }): Fields<ScopedGrant, _>)
        |> field "role" _.Role (VocabularyCodec.create Role.vocabulary)
        |> field "scope" _.Scope grantScope

    let authorityGrant = authorityGrantFields |> build |> named "AuthorityGrant"

    let grantsPage =
        (record (fun v0 v1 v2 ->
            {
                AuthorityGrantPage.Actor = v0
                Items = v1
                NextCursor = v2
            })
        : Fields<AuthorityGrantPage, _>)
        |> field "actor" _.Actor actorSummary
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

    let historyDifference =
        (record (fun lifecycle fields ->
            {
                HistoryDifference.Lifecycle = lifecycle
                Fields = fields
            })
        : Fields<HistoryDifference, _>)
        |> field "lifecycle" _.Lifecycle (nullable LedgerCodecs.lifecycleChange)
        |> field "fields" _.Fields (list CaseFields.declarations.Length fieldChange)
        |> build
        |> named "HistoryDifference"

    let private fullEntry receiptCodec =
        (record (fun receipt difference ->
            {
                FullHistoryEntry.Receipt = receipt
                Difference = difference
            })
        : Fields<FullHistoryEntry, _>)
        |> fixedField "kind" "FULL"
        |> field "receipt" _.Receipt receiptCodec
        |> field "difference" _.Difference (nullable historyDifference)
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
        (record (fun id reference entries next ->
            {
                CaseId = id
                Reference = reference
                Entries = entries
                NextCursor = next
            })
        : Fields<HistoryResultPage, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.Reference caseReference
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

    let caseRef =
        (record (fun id reference ->
            {
                CaseRef.CaseId = id
                CaseReference = reference
            })
        : Fields<CaseRef, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.CaseReference caseReference
        |> build
        |> named "CaseRef"

    let casesPage =
        (record (fun items next offers basis exact ->
            {
                Basis = basis
                Items = items
                NextCursor = next
                Offers = offers
                ExactReference = exact
            })
        : Fields<CaseSummaryPage, _>)
        |> field "items" _.Items (list 50 caseSummary)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumCaseCursorCharacters))
        |> field "offers" _.Offers (list maximumInstallationOffers installationOffer)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "exactReference" _.ExactReference (nullable caseRef)
        |> build
        |> named "CasesPage"

    let assigneePage =
        (record (fun items next ->
            {
                AssigneePage.Items = items
                NextCursor = next
            })
        : Fields<AssigneePage, _>)
        |> field "items" _.Items (list Paging.MaximumPageSize ActorCodecs.actorRef)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "AssigneePage"
