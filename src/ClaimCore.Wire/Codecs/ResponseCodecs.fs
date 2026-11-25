namespace ClaimCore.Wire

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open System
open System.Globalization

module ResponseCodecs =
    open Codec
    open ResponseScalars

    let actorRef = ActorCodecs.actorRef

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
        |> List.map (fun field ->
            let declaration = BusinessField.describe field
            let value = CaseFieldCodecs.scalar declaration.Kind

            let value =
                if declaration.AllowsAbsence then
                    nullable value
                else
                    value |> map Some Option.get

            field, declaration.Key, declaration.AllowsAbsence, value)
        |> Codec.fieldMapWithPresence
        |> interpret
            (fun path fields ->
                fields
                |> Map.toList
                |> List.choose (fun (field, value) ->
                    value |> Option.map (fun value -> field, value))
                |> create
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> List.map (fun (field, value) -> field, Some value) >> Map.ofList)

    let private registration =
        group RecordGroup.Registration Registration.create Registration.values

    let private decision = group RecordGroup.Decision Decision.create Decision.values

    let financialPosition = ClaimCore.RecordFormat.FinancialCodecs.position decision

    let caseFields =
        (record (fun reference handling registration finance ->
            {
                CaseFields.CaseReference = reference
                Handling = handling
                Registration = registration
                Finance = finance
            })
        : Fields<CaseFields, _>)
        |> field (BusinessField.key BusinessField.CaseReference) _.CaseReference caseReference
        |> field "handling" _.Handling ClaimCore.RecordFormat.HandlingCodecs.value
        |> field "registration" _.Registration registration
        |> field "finance" _.Finance financialPosition
        |> build
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

    let private historyAccess = VocabularyCodec.create HistoryAccess.vocabulary

    let private peopleKind = TaskVocabulary.forSubject ActSubject.People
    let private maximumInstallationOffers = 1 + TaskVocabulary.count ActSubject.People

    let private profileInput =
        (record (fun name contact email ->
            {
                ActorProfileInput.DisplayName = name
                Contact = contact
                DigestEmail = email
            })
        : Fields<ActorProfileInput, _>)
        |> field "displayName" _.DisplayName text
        |> field "contact" _.Contact (nullable text)
        |> optionalField "digestEmail" _.DigestEmail text
        |> build
        |> named "ActorProfileInput"

    let private dateValue =
        date
        |> map
            (fun value ->
                DateOnly.ParseExact(value, CalendarDate.ExactFormat, CultureInfo.InvariantCulture))
            (fun value -> value.ToString("O", CultureInfo.InvariantCulture))

    let correctionAdvice =
        (record (fun registration decision ->
            {
                CorrectionChoices.Registration = registration
                Decision = decision
            })
        : Fields<CorrectionChoices, _>)
        |> field
            "registration"
            _.Registration
            (list
                2
                (VocabularyCodec.select
                    (fun value ->
                        List.contains
                            value
                            (CorrectionGroupAction.actions RecordGroup.Registration))
                    CorrectionGroupAction.vocabulary))
        |> field
            "decision"
            _.Decision
            (list
                (CorrectionGroupAction.actions RecordGroup.Decision).Length
                (VocabularyCodec.select
                    (fun value ->
                        List.contains value (CorrectionGroupAction.actions RecordGroup.Decision))
                    CorrectionGroupAction.vocabulary))
        |> build
        |> named "CorrectionAdvice"

    let registerMaintenance =
        (record RegisterMaintenance.create: Fields<RegisterMaintenance, _>)
        |> field "generation" _.Generation (integer 0L SafeInteger.Maximum)
        |> field "pauseId" _.PauseId (nullable uuid)
        |> build
        |> named "RegisterMaintenance"

    let private blockedCaseAct =
        (record (fun act reason ->
            {
                CaseTaskRefusal.Act = act
                Reason = reason
            })
        : Fields<CaseTaskRefusal, _>)
        |> field "act" _.Act (TaskVocabulary.forSubject ActSubject.Case)
        |> field "reason" _.Reason DiagnosticCodecs.caseActAvailability
        |> build
        |> named "CaseTaskRefusal"

    let caseAdvice =
        record (fun acts correction history maintenance recommended blocked ->
            acts, correction, history, maintenance, recommended, blocked)
        |> field
            "acts"
            (fun (acts, _, _, _, _, _) -> acts)
            (list (TaskVocabulary.count ActSubject.Case) (TaskVocabulary.forSubject ActSubject.Case))
        |> optionalField
            "correction"
            (fun (_, correction, _, _, _, _) -> correction)
            correctionAdvice
        |> field "history" (fun (_, _, history, _, _, _) -> history) historyAccess
        |> field "maintenance" (fun (_, _, _, maintenance, _, _) -> maintenance) registerMaintenance
        |> field
            "recommended"
            (fun (_, _, _, _, recommended, _) -> recommended)
            (nullable (TaskVocabulary.forSubject ActSubject.Case))
        |> field
            "blocked"
            (fun (_, _, _, _, _, blocked) -> blocked)
            (list (TaskVocabulary.count ActSubject.Case) blockedCaseAct)
        |> build
        |> interpret
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
        |> enumPartition
            (TaskVocabulary.all
             |> List.filter (fun id ->
                 (ClaimCore.Domain.Tasks.Registry.findDeclaration id |> Option.get).Target =
                     ActSubject.Case)
             |> List.map TaskId.token)
            "acts"
            "blocked"
            "act"
        |> named "CaseAdvice"

    let caseView =
        (record
            (fun
                id
                reference
                revision
                view
                advice
                assigned
                work
                headline
                attention
                stages
                disposition
                nextActions
                notes
                parties
                partyOffers
                latestPayments
                paymentPayees
                changedAt
                recordedBy
                family
                matchedReference
                relatedCases ->
                {
                    MatchedReference = matchedReference
                    RelatedCases = defaultArg relatedCases RelatedCaseLinks.empty
                    FamilySequence = family
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
                    Disposition = disposition
                    NextActions = nextActions
                    Notes = notes
                    Parties = parties
                    PartyOffers = partyOffers |> List.map TaskOffer.Declared
                    LatestPayments = latestPayments
                    PaymentPayees = paymentPayees
                    ChangedAt = changedAt
                    RecordedBy = recordedBy
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
        |> field "disposition" _.Disposition (nullable CaseStateCodecs.disposition)
        |> field "nextActions" _.NextActions (list Paging.MaximumPageSize WorkCodecs.reminderRead)
        |> field "notes" _.Notes WorkCodecs.notes
        |> field "parties" _.Parties (list PartyContent.MaximumCurrent WorkCodecs.party)
        |> field
            "partyActs"
            (fun value -> value.PartyOffers |> List.map TaskOffer.id)
            (list 1 (TaskVocabulary.forSubject ActSubject.Party))
        |> field
            "latestPayments"
            _.LatestPayments
            (list PaymentPaging.PreviewLimit PaymentCodecs.entry)
        |> field
            "paymentPayees"
            _.PaymentPayees
            (list PaymentPaging.PreviewLimit PaymentCodecs.payee)
        |> field "changedAt" _.ChangedAt instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field "familySequence" _.FamilySequence ResponseScalars.sequence
        |> optionalField "matchedReference" _.MatchedReference caseReference
        |> optionalField
            "relatedCases"
            (fun reply ->
                if reply.RelatedCases = RelatedCaseLinks.empty then
                    None
                else
                    Some reply.RelatedCases)
            WorkCodecs.relatedCases
        |> build
        |> named "CaseView"

    let installationOffer =
        tagged
            "InstallationOffer"
            [
                unitVariant "case.open" InstallationOffer.Open
                variant
                    "PEOPLE_ACT"
                    InstallationOffer.PeopleAct
                    (function
                    | InstallationOffer.PeopleAct act -> Some act
                    | _ -> None)
                    (record id |> field "act" id peopleKind)
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
                issuer
                roles ->
                {
                    ActorContext.Issuer = issuer
                    Roles = roles
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
        |> field "roles" _.Roles (list Role.all.Length actorRoleSummary)

    let actorContext = actorContextFields |> build |> named "ActorContext"

    let requesterClaims =
        let hint =
            partyName
            |> restrict
                (Schema.String(None, None, Some 1, Some PartyName.MaximumCharacters, None))
                (fun path value -> (boundedText 1 PartyName.MaximumCharacters).Validate path value)
            |> refine (PartyName.admit >> Result.isOk)

        (record (fun name username email verified ->
            RequesterClaims.admit
                (name |> Option.map PartyName.format)
                (username |> Option.map PartyName.format)
                (email |> Option.map PartyName.format)
                verified)
        : Fields<RequesterClaims, _>)
        |> optionalField "name" _.Name hint
        |> optionalField "preferredUsername" _.PreferredUsername hint
        |> optionalField "email" _.Email hint
        |> field "emailVerified" _.EmailVerified boolean
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
        (record (fun basis items cursor ->
            {
                Basis = basis
                AccessRequestPage.Items = items
                NextCursor = cursor
            })
        : Fields<AccessRequestPage, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "items" _.Items (list Paging.MaximumPageSize accessRequest)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumAccessRequestCursorCharacters))
        |> build
        |> named "AccessRequestPage"

    let private disclosureSelection =
        let pair =
            record (fun key value -> key, value)
            |> field "key" fst text
            |> field "value" snd text
            |> build

        list 64 pair
        |> Codec.interpret
            (fun path values ->
                let selection = Map.ofList values

                if selection.Count = values.Length then
                    Ok selection
                else
                    Error
                        {
                            Path = path
                            Reason = "Disclosure selection keys must be unique."
                        })
            Map.toList

    let private disclosureExport =
        (record (fun query selection count ->
            {
                DisclosureExport.Query = query
                Selection = selection
                RowCount = count
            })
        : Fields<DisclosureExport, _>)
        |> field "query" _.Query text
        |> field "selection" _.Selection disclosureSelection
        |> field "rowCount" _.RowCount (integer 0L (int64 Int32.MaxValue) |> map int int64)
        |> build
        |> named "DisclosureExport"

    let private disclosureEvent =
        let attachmentId =
            uuid |> map (AttachmentId.create >> Option.get) AttachmentId.value

        (record (fun actor kind caseId attachment outcome export ->
            {
                DisclosureEvent.ActorId = actor
                Kind = kind
                CaseId = caseId
                AttachmentId = attachment
                Outcome = outcome
                Export = export
            })
        : Fields<DisclosureEvent, _>)
        |> field "actorId" _.ActorId actorId
        |> field "kind" _.Kind (VocabularyCodec.create DisclosureKind.vocabulary)
        |> field "caseId" _.CaseId (nullable caseId)
        |> field "attachmentId" _.AttachmentId (nullable attachmentId)
        |> field "outcome" _.Outcome (VocabularyCodec.create DisclosureOutcome.vocabulary)
        |> field "export" _.Export (nullable disclosureExport)
        |> build
        |> named "DisclosureEvent"

    let private recordedDisclosure =
        (record (fun id instant event ->
            {
                RecordedDisclosure.Id = id
                RecordedAt = instant
                Event = event
            })
        : Fields<RecordedDisclosure, _>)
        |> field "id" _.Id (integer 1L JsonValue.MaximumInteger)
        |> field "recordedAt" _.RecordedAt instant
        |> field "event" _.Event disclosureEvent
        |> build
        |> named "RecordedDisclosure"

    let disclosureTrailPage =
        (record (fun basis items cursor ->
            {
                DisclosureTrailPage.Basis = basis
                Items = items
                NextCursor = cursor
            })
        : Fields<DisclosureTrailPage, _>)
        |> field "basis" _.Basis workDateBasis
        |> field "items" _.Items (list Paging.MaximumPageSize recordedDisclosure)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumDisclosureTrailCursorCharacters))
        |> build
        |> named "DisclosureTrailPage"

    let position = LedgerCodecs.position

    let private reviewChanges =
        (record (fun lifecycle fields payments ->
            {
                CaseReviewChanges.Lifecycle = lifecycle
                Fields = fields
                Payments = payments
            })
        : Fields<CaseReviewChanges, _>)
        |> field "lifecycle" _.Lifecycle (nullable LedgerCodecs.lifecycleChange)
        |> field
            "fields"
            _.Fields
            (list (Vocabulary.all BusinessField.vocabulary).Length TimelineCodecs.fieldDifference)
        |> field "payments" _.Payments (nullable PaymentCodecs.changes)
        |> build
        |> named "CaseReviewChanges"

    let reviewFields =
        (record (fun before proposed changes context ->
            {
                AdvisoryReview.Before = before
                Proposed = proposed
                Changes = changes
                Context = context
            })
        : Fields<AdvisoryReview, _>)
        |> field "before" _.Before (nullable caseSnapshot)
        |> field "after" _.Proposed caseSnapshot
        |> field "changes" _.Changes reviewChanges
        |> field "context" _.Context runtimeContext

    let private actorResponsibleCases =
        let lifecycle = CaseListFilter.activeOpen<unit>.Lifecycles |> List.exactlyOne
        let status = CaseListFilter.activeOpen<unit>.Statuses |> List.exactlyOne

        (record (fun count actor _view _lifecycle _status ->
            {
                ActorResponsibleCases.Count = count
                ResponsibleActor = actor
            })
        : Fields<ActorResponsibleCases, _>)
        |> field "count" _.Count (integer 0L SafeInteger.Maximum)
        |> field "responsibleActor" _.ResponsibleActor actorId
        |> field "view" _.View (constant "ALL" |> map (fun () -> CaseView.All) (fun _ -> ()))
        |> field
            "lifecycle"
            _.Lifecycle
            (constant (Lifecycle.token lifecycle) |> map (fun () -> lifecycle) (fun _ -> ()))
        |> field
            "status"
            _.Status
            (constant (CaseStatus.token status) |> map (fun () -> status) (fun _ -> ()))
        |> build
        |> named "ActorResponsibleCases"

    let actorSummary =
        let contact =
            boundedUtf8Text 1 PartyName.MaximumBytes
            |> interpret
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

    let internal peopleGrantFields =
        (record (fun v0 v1 -> { ScopedGrant.Role = v0; Scope = v1 }): Fields<ScopedGrant, _>)
        |> field "role" _.Role (VocabularyCodec.create Role.vocabulary)
        |> field "scope" _.Scope grantScope

    let peopleGrant = peopleGrantFields |> build |> named "PeopleGrant"

    let private grantRead =
        (record (fun role scope at by ->
            {
                GrantReadModel.Role = role
                Controls = []
                Scope = scope
                GrantedAt = at
                GrantedBy = by
            })
        : Fields<GrantReadModel, _>)
        |> field "role" _.Role (VocabularyCodec.create Role.vocabulary)
        |> field "scope" _.Scope grantScope
        |> field "grantedAt" _.GrantedAt instant
        |> field "grantedBy" _.GrantedBy ActorCodecs.authorityRef
        |> build
        |> named "GrantReadModel"

    let grantsPage =
        (record (fun basis v0 revision offers seed canTerminate v1 v2 ->
            {
                Basis = basis
                PeopleGrantPage.Actor = v0
                Controls = []
                Revision = revision
                Offers = offers
                ProfileSeed = seed
                CanTerminateSessions = canTerminate
                Items = v1
                NextCursor = v2
            })
        : Fields<PeopleGrantPage, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "actor" _.Actor actorSummary
        |> field "revision" _.Revision positiveRevision
        |> field "offers" _.Offers (list (TaskVocabulary.count ActSubject.People) peopleKind)
        |> field "profileSeed" _.ProfileSeed (nullable profileInput)
        |> field "canTerminateSessions" _.CanTerminateSessions boolean
        |> field "items" _.Items (list 50 grantRead)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "GrantsPage"

    let actorsPage =
        (record (fun basis v0 v1 ->
            {
                Basis = basis
                ActorReviewPage.Items = v0
                NextCursor = v1
            })
        : Fields<ActorReviewPage, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "items" _.Items (list 50 actorSummary)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "ActorsPage"

    let logoutLocation =
        (record (fun value -> { LogoutLocation.Location = value }): Fields<LogoutLocation, _>)
        |> field "location" _.Location nonempty
        |> build
        |> named "LogoutLocation"

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

    let private exactCaseReference =
        (record (fun id reference matched ->
            {
                ExactCaseReference.CaseId = id
                CaseReference = reference
                MatchedReference = matched
            })
        : Fields<ExactCaseReference, _>)
        |> field "caseId" _.CaseId caseId
        |> field "caseReference" _.CaseReference caseReference
        |> field "matchedReference" _.MatchedReference (nullable caseReference)
        |> build
        |> named "ExactCaseReference"

    let casesPage =
        (record (fun items next offers basis exact authorizedScopeEmpty ->
            {
                Basis = basis
                Items = items
                NextCursor = next
                Offers = offers
                ExactReference = exact
                AuthorizedScopeEmpty = authorizedScopeEmpty
            })
        : Fields<CaseSummaryPage, _>)
        |> field "items" _.Items (list 50 caseSummary)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumCaseCursorCharacters))
        |> field "offers" _.Offers (list maximumInstallationOffers installationOffer)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "exactReference" _.ExactReference (nullable exactCaseReference)
        |> field "authorizedScopeEmpty" _.AuthorizedScopeEmpty boolean
        |> build
        |> named "CasesPage"

    let private partyChoice =
        (record (fun id name role ->
            {
                PartyId = id
                Name = name
                Role = role
            })
        : Fields<PartyChoice, _>)
        |> field "partyId" _.PartyId ResponseScalars.partyId
        |> field "name" _.Name (nullable ResponseScalars.partyName)
        |> field "role" _.Role (VocabularyCodec.create PartyRole.vocabulary)
        |> build
        |> named "PartyChoice"

    let partyChoicePage =
        (record (fun basis items next ->
            {
                PartyChoicePage.Basis = basis
                Items = items
                NextCursor = next
            })
        : Fields<PartyChoicePage, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "items" _.Items (list Paging.MaximumPageSize partyChoice)
        |> field
            "nextCursor"
            _.NextCursor
            (nullable (boundedText 1 Paging.MaximumActorCursorCharacters))
        |> build
        |> named "PartyChoicePage"

    let assigneePage =
        (record (fun caseId revision basis items next ->
            {
                AssigneePage.CaseId = caseId
                WorkRevision = revision
                Basis = basis
                Items = items
                NextCursor = next
            })
        : Fields<AssigneePage, _>)
        |> field "caseId" _.CaseId ResponseScalars.caseId
        |> field "workRevision" _.WorkRevision ResponseScalars.revision
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "items" _.Items (list Paging.MaximumPageSize ActorCodecs.actorRef)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "AssigneePage"
