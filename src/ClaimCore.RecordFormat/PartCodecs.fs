namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Domain.Aggregates
open ClaimCore.Domain.Changes
open ClaimCore.Json

/// One retained/private codec pair per part kind. Current admission remains with the tasks.
module PartCodecs =
    open Codec
    open RecordScalars

    let private wrong () =
        invalidArg "value" "The value does not belong to this part codec."

    let private case get =
        function
        | PartValue.Case value -> get value
        | _ -> wrong ()

    let private payment get =
        function
        | PartValue.Payment value -> get value
        | _ -> wrong ()

    let private reminder get =
        function
        | PartValue.Reminder value -> get value
        | _ -> wrong ()

    let private note get =
        function
        | PartValue.Note value -> get value
        | _ -> wrong ()

    let private party get =
        function
        | PartValue.Party value -> get value
        | _ -> wrong ()

    let private person get =
        function
        | PartValue.Person value -> get value
        | _ -> wrong ()

    let aggregate =
        tagged
            "Aggregate"
            [
                variant
                    "FAMILY"
                    AggregateReference.Family
                    (function
                    | AggregateReference.Family value -> Some value
                    | _ -> None)
                    (record id |> field "id" id caseId)
                variant
                    "PERSON"
                    AggregateReference.Person
                    (function
                    | AggregateReference.Person value -> Some value
                    | _ -> None)
                    (record id |> field "id" id actorId)
            ]

    let owner =
        tagged
            "CompartmentOwner"
            [
                variant
                    "FAMILY"
                    ClaimCore.Domain.Changes.CompartmentOwner.Family
                    (function
                    | ClaimCore.Domain.Changes.CompartmentOwner.Family value -> Some value
                    | _ -> None)
                    (record id |> field "id" id caseId)
                variant
                    "PROFILE"
                    ClaimCore.Domain.Changes.CompartmentOwner.Profile
                    (function
                    | ClaimCore.Domain.Changes.CompartmentOwner.Profile value -> Some value
                    | _ -> None)
                    (record id |> field "id" id actorId)
            ]

    let principal =
        (record PrincipalKey.ofKind: Fields<PrincipalKey, _>)
        |> field
            "kind"
            (fun value -> let kind, _, _ = PrincipalKey.parts value in kind)
            (VocabularyCodec.create PrincipalKind.vocabulary)
        |> field "issuer" (fun value -> let _, issuer, _ = PrincipalKey.parts value in issuer) text
        |> field
            "subject"
            (fun value -> let _, _, subject = PrincipalKey.parts value in subject)
            text
        |> buildResult (fun path _ ->
            {
                Path = path
                Reason = "Invalid retained principal."
            })

    let private scope =
        tagged
            "GrantScopeValue"
            [
                unitVariant (ScopeKind.token ScopeKind.Installation) Scope.Installation
                variant
                    (ScopeKind.token ScopeKind.Case)
                    Scope.Case
                    (function
                    | Scope.Case id -> Some id
                    | _ -> None)
                    (record id |> field "caseId" id caseId)
            ]
        |> vocabulary (Vocabulary.name ScopeKind.vocabulary)

    let private assignment =
        (record (fun role scope active ->
            {
                GrantAssignment.Grant = { Role = role; Scope = scope }
                Active = active
            })
        : Fields<GrantAssignment, _>)
        |> field "role" (_.Grant >> _.Role) (VocabularyCodec.create Role.vocabulary)
        |> field "scope" (_.Grant >> _.Scope) scope
        |> field "active" _.Active boolean
        |> build

    let profile =
        (record (fun name contact ->
            {
                ActorProfile.DisplayName = name
                Contact = contact
            })
        : Fields<ActorProfile, _>)
        |> field "displayName" _.DisplayName (scalar PartyName.parse PartyName.format)
        |> field "contact" _.Contact (nullable (scalar ContactText.parse ContactText.format))
        |> build

    let work =
        (record (fun responsible summary ->
            {
                CaseWork.Responsible = responsible
                Summary = summary
            })
        : Fields<CaseWork, _>)
        |> field "responsibleActor" _.Responsible (nullable actorId)
        |> field "handlingSummary" _.Summary (nullable summary)
        |> build

    let private caseRetained =
        (record (fun reference lifecycle status ->
            PartValue.Case
                {
                    Reference = reference
                    Lifecycle = lifecycle
                    Status = status
                    Content = None
                    Disposition = None
                })
        : Fields<PartValue, _>)
        |> optionalField
            "reference"
            (case _.Reference)
            (scalar CaseReference.parse CaseReference.format)
        |> field "lifecycle" (case _.Lifecycle) (VocabularyCodec.create Lifecycle.vocabulary)
        |> field "status" (case _.Status) (VocabularyCodec.create CaseStatus.vocabulary)
        |> build
        |> named "PartRetained.CASE"

    let private paymentRetained =
        (record (fun removed -> PartValue.Payment { Removed = removed; Entry = None })
        : Fields<PartValue, _>)
        |> field "removed" (payment _.Removed) boolean
        |> build
        |> named "PartRetained.PAYMENT"

    let private workRetained =
        record (PartValue.Work None) |> build |> named "PartRetained.WORK"

    let private reminderRetained =
        (record (fun state -> PartValue.Reminder { State = state; Plan = None })
        : Fields<PartValue, _>)
        |> field "state" (reminder _.State) (VocabularyCodec.create ReminderState.vocabulary)
        |> build
        |> named "PartRetained.REMINDER"

    let private noteRetained =
        (record (fun removed -> PartValue.Note { Removed = removed; Text = None })
        : Fields<PartValue, _>)
        |> field "removed" (note _.Removed) boolean
        |> build
        |> named "PartRetained.NOTE"

    let private partyRetained =
        (record (fun removed -> PartValue.Party { Removed = removed; Content = None })
        : Fields<PartValue, _>)
        |> field "removed" (party _.Removed) boolean
        |> build
        |> named "PartRetained.PARTY"

    let private personRetained =
        (record (fun principal enabled grants ->
            PartValue.Person
                {
                    Principal = principal
                    Enabled = enabled
                    Grants = grants
                    Profile = ProfileValue.Absent
                })
        : Fields<PartValue, _>)
        |> optionalField "principal" (person _.Principal) principal
        |> field "enabled" (person _.Enabled) boolean
        |> field "grants" (person _.Grants) (boundedList 0 PeopleAdmission.MaximumGrants assignment)
        |> build
        |> named "PartRetained.PERSON"

    let retained =
        function
        | PartKind.Case -> caseRetained
        | PartKind.Payment -> paymentRetained
        | PartKind.Work -> workRetained
        | PartKind.Reminder -> reminderRetained
        | PartKind.Note -> noteRetained
        | PartKind.Party -> partyRetained
        | PartKind.Person -> personRetained

    let withCompartment present =
        function
        | PartValue.Person value ->
            PartValue.Person
                { value with
                    Profile =
                        if present then
                            ProfileValue.Present None
                        else
                            ProfileValue.Absent
                }
        | value -> value

    let hasPrivate =
        function
        | PartValue.Case value -> value.Lifecycle <> Lifecycle.Erased
        | PartValue.Payment _
        | PartValue.Work _
        | PartValue.Reminder _
        | PartValue.Party _ -> true
        | PartValue.Note value -> not value.Removed
        | PartValue.Person value -> value.Profile <> ProfileValue.Absent

    let private available =
        function
        | PartValue.Case value -> value.Lifecycle = Lifecycle.Erased || value.Content.IsSome
        | PartValue.Payment value -> value.Entry.IsSome
        | PartValue.Work value -> value.IsSome
        | PartValue.Reminder value -> value.Plan.IsSome
        | PartValue.Note value -> value.Removed || value.Text.IsSome
        | PartValue.Party value -> value.Content.IsSome
        | PartValue.Person value ->
            match value.Profile with
            | ProfileValue.Present None -> false
            | _ -> true

    let private casePrivate (retained: CaseValue) =
        let content = case (fun value -> value.Content |> Option.defaultWith wrong)

        let reason =
            case (fun value ->
                value.Disposition
                |> Option.map (function
                    | CaseDisposition.Voided reason
                    | CaseDisposition.Reinstated reason -> reason))

        (record (fun registration finance reason ->
            let content =
                {
                    CaseContent.Registration = registration
                    Finance = finance
                }

            CaseFamily.validateContent content
            |> Result.bind (fun () ->
                if retained.Lifecycle = Lifecycle.Erased then
                    Error RecordViolation.InvalidSubjectState
                else
                    let disposition =
                        reason
                        |> Option.map (fun reason ->
                            if retained.Lifecycle = Lifecycle.Voided then
                                CaseDisposition.Voided reason
                            else
                                CaseDisposition.Reinstated reason)

                    Ok(
                        PartValue.Case
                            { retained with
                                Content = Some content
                                Disposition = disposition
                            }
                    )))
        : Fields<PartValue, _>)
        |> field "registration" (content >> _.Registration) RecordCodecs.registration
        |> field "finance" (content >> _.Finance) (FinancialCodecs.position RecordCodecs.decision)
        |> optionalField "reason" reason (scalar Reason.parse Reason.format)
        |> buildResult (fun path _ ->
            {
                Path = path
                Reason = "Invalid accepted case value."
            })

    let private paymentPrivate id (retained: PaymentValue) =
        let entry = payment (fun value -> value.Entry |> Option.defaultWith wrong)

        (record (fun date amount ->
            PaymentEntry.create id date amount
            |> Result.map (fun entry -> PartValue.Payment { retained with Entry = Some entry }))
        : Fields<PartValue, _>)
        |> field "paymentDate" (entry >> PaymentEntry.date) date
        |> field "paidAmount" (entry >> PaymentEntry.amount) (scalar Amount.parse Amount.format)
        |> buildResult (fun path reason -> { Path = path; Reason = reason })

    let private reminderPrivate (retained: ReminderValue) =
        let plan = reminder (fun value -> value.Plan |> Option.defaultWith wrong)

        (record (fun action due ->
            PartValue.Reminder
                { retained with
                    Plan = Some { Action = action; Due = due }
                })
        : Fields<PartValue, _>)
        |> field "action" (plan >> _.Action) action
        |> field "due" (plan >> _.Due) date
        |> build

    let partyContent =
        (record PartyContent.create: Fields<PartyContent, _>)
        |> field "role" _.Role (VocabularyCodec.create PartyRole.vocabulary)
        |> field "name" _.Name (nullable (scalar PartyName.parse PartyName.format))
        |> field "details" _.Details (nullable (scalar PartyDetails.parse PartyDetails.format))
        |> buildResult (fun path _ ->
            {
                Path = path
                Reason = "At least one party name or detail must be present."
            })
        |> named "PartyContent"

    let privateValueCodec partId value =
        match value with
        | PartValue.Case retained -> casePrivate retained
        | PartValue.Payment retained ->
            let id =
                OperationId.create partId
                |> Option.defaultWith (fun () ->
                    invalidArg "partId" "A payment part is an operation identity.")

            paymentPrivate id retained
        | PartValue.Work _ ->
            work
            |> map (Some >> PartValue.Work) (function
                | PartValue.Work(Some value) -> value
                | _ -> wrong ())
        | PartValue.Reminder retained -> reminderPrivate retained
        | PartValue.Note retained ->
            scalar NoteText.parse NoteText.format
            |> map
                (fun text -> PartValue.Note { retained with Text = Some text })
                (note (fun value -> value.Text |> Option.defaultWith wrong))
        | PartValue.Party retained ->
            partyContent
            |> map
                (fun content -> PartValue.Party { retained with Content = Some content })
                (party (fun value -> value.Content |> Option.defaultWith wrong))
        | PartValue.Person retained ->
            profile
            |> map
                (fun profile ->
                    PartValue.Person
                        { retained with
                            Profile = ProfileValue.Present(Some profile)
                        })
                (person (fun value -> ProfileValue.value value.Profile |> Option.defaultWith wrong))

    let privateValue partId value =
        let valid =
            match value with
            | PartValue.Case value ->
                (value.Lifecycle <> Lifecycle.Erased
                 || (value.Content.IsNone && value.Disposition.IsNone))
                && (match value.Disposition with
                    | None -> true
                    | Some(CaseDisposition.Voided _) -> value.Lifecycle = Lifecycle.Voided
                    | Some(CaseDisposition.Reinstated _) -> value.Lifecycle = Lifecycle.Active)
            | PartValue.Payment value ->
                value.Entry
                |> Option.forall (fun entry -> OperationId.value (PaymentEntry.id entry) = partId)
            | PartValue.Note value -> not value.Removed || value.Text.IsNone
            | _ -> true

        if not valid then
            Error
                {
                    Field = "$.values"
                    Message = "Accepted part values disagree with their retained identity or state."
                }
        elif not (hasPrivate value) then
            Ok None
        elif not (available value) then
            Error
                {
                    Field = "$.values"
                    Message = "Private accepted values are unavailable for append."
                }
        else
            Ok(Some((privateValueCodec partId value).Write value))
