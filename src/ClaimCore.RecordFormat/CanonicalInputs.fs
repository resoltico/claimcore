namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

/// The canonical codecs for authored business inputs; envelopes and selectors belong to callers.
module CanonicalInputs =
    open Codec

    let private tagged vocabulary name variants =
        Codec.taggedOn "kind" name variants
        |> Codec.vocabulary (Vocabulary.name vocabulary)

    let reminderRevision =
        integer (Revision.value Revision.first) JsonValue.MaximumInteger

    let private inputFields group updating =
        CaseFields.ofGroup group
        |> List.map (fun field ->
            let declaration = BusinessField.describe field

            let value =
                if declaration.AllowsAbsence then
                    nullable text
                else
                    text |> map Some Option.get

            field, declaration.Key, updating || declaration.AllowsAbsence, value)
        |> Codec.fieldMapWithPresence

    let private inputGroup group create values =
        inputFields group false
        |> mapResult
            (fun path fields ->
                fields
                |> Map.toList
                |> List.choose (fun (field, value) ->
                    value |> Option.map (fun value -> field, value))
                |> create
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> List.map (fun (field, value) -> field, Some value) >> Map.ofList)

    let registration =
        inputGroup RecordGroup.Registration RegistrationInput.create RegistrationInput.values
        |> named "Registration"

    let decision =
        inputGroup RecordGroup.Decision DecisionInput.create DecisionInput.values
        |> named "Decision"

    let payment =
        inputGroup RecordGroup.Payment PaymentInput.create PaymentInput.values
        |> named "Payment"

    let private update group create values =
        inputFields group true
        |> mapResult
            (fun path fields ->
                create (Map.toList fields)
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> Map.ofList)

    let registrationUpdate =
        update
            RecordGroup.Registration
            RegistrationUpdateInput.create
            RegistrationUpdateInput.values
        |> named "RegistrationUpdate"

    let decisionUpdate =
        update RecordGroup.Decision DecisionUpdateInput.create DecisionUpdateInput.values
        |> named "DecisionUpdate"

    let private workValue name =
        Codec.taggedOn
            "kind"
            name
            [
                unitVariant
                    (Vocabulary.token WorkChoice.vocabulary WorkChoice.Clear)
                    WorkValue.Clear
                variant
                    (Vocabulary.token WorkChoice.vocabulary WorkChoice.Set)
                    WorkValue.Set
                    (function
                    | WorkValue.Set value -> Some value
                    | _ -> None)
                    (record id |> field "value" id text)
            ]
        |> Codec.vocabulary (Vocabulary.name WorkChoice.vocabulary)

    let work =
        let supplied =
            function
            | WorkValue.Keep -> None
            | choice -> Some choice

        (record (fun actor summary ->
            {
                CaseWorkInput.Responsible = defaultArg actor WorkValue.Keep
                Summary =
                    match summary with
                    | None -> WorkValue.Keep
                    | Some None -> WorkValue.Clear
                    | Some(Some value) -> WorkValue.Set value
            })
        : Fields<CaseWorkInput, _>)
        |> optionalField
            "responsibleActor"
            (_.Responsible >> supplied)
            (workValue "ResponsibleActorChange")
        |> optionalField
            "handlingSummary"
            (fun value ->
                match value.Summary with
                | WorkValue.Keep -> None
                | WorkValue.Clear -> Some None
                | WorkValue.Set text -> Some(Some text))
            (nullable text)
        |> build
        |> named "CaseWorkUpdate"

    let workAct =
        tagged
            WorkActKind.vocabulary
            "WorkAct"
            [
                variant
                    "UPDATE_WORK"
                    WorkAct.UpdateWork
                    (function
                    | WorkAct.UpdateWork value -> Some value)
                    (record id |> field "values" id work)
            ]

    let private planFields =
        (record (fun action due ->
            {
                ReminderPlanInput.Action = action
                Due = due
            })
        : Fields<ReminderPlanInput, _>)
        |> field "action" _.Action text
        |> field "dueDate" _.Due text

    let private reminderTargetFields =
        record (fun id revision -> id, revision)
        |> field "reminderId" fst uuid
        |> field "expectedRevision" snd reminderRevision

    let private reminderPlanFields targetFields =
        record (fun (id, revision) plan -> id, revision, plan)
        |> flatten (fun (id, revision, _) -> id, revision) targetFields
        |> flatten (fun (_, _, plan) -> plan) planFields

    let reminderActWith targetFields =
        tagged
            ReminderActKind.vocabulary
            "ReminderAct"
            [
                variant
                    "CREATE"
                    ReminderAct.Create
                    (function
                    | ReminderAct.Create value -> Some value
                    | _ -> None)
                    planFields
                variant
                    "UPDATE"
                    (fun (id, revision, plan) -> ReminderAct.Update(id, revision, plan))
                    (function
                    | ReminderAct.Update(id, revision, plan) -> Some(id, revision, plan)
                    | _ -> None)
                    (reminderPlanFields targetFields)
                variant
                    "COMPLETE"
                    (fun (id, revision) -> ReminderAct.Complete(id, revision))
                    (function
                    | ReminderAct.Complete(id, revision) -> Some(id, revision)
                    | _ -> None)
                    targetFields
                variant
                    "CANCEL"
                    (fun (id, revision) -> ReminderAct.Cancel(id, revision))
                    (function
                    | ReminderAct.Cancel(id, revision) -> Some(id, revision)
                    | _ -> None)
                    targetFields
                variant
                    "REOPEN"
                    (fun (id, revision, due) -> ReminderAct.Reopen(id, revision, due))
                    (function
                    | ReminderAct.Reopen(id, revision, due) -> Some(id, revision, due)
                    | _ -> None)
                    (record (fun (id, revision) due -> id, revision, due)
                     |> flatten (fun (id, revision, _) -> id, revision) targetFields
                     |> field "dueDate" (fun (_, _, due) -> due) text)
                variant
                    "COMPLETE_AND_PLAN_NEXT"
                    (fun (id, revision, plan) ->
                        ReminderAct.CompleteAndPlanNext(id, revision, plan))
                    (function
                    | ReminderAct.CompleteAndPlanNext(id, revision, plan) ->
                        Some(id, revision, plan)
                    | _ -> None)
                    (reminderPlanFields targetFields)
            ]

    let reminderAct = reminderActWith reminderTargetFields

    let registrationGroup =
        tagged
            CorrectionGroupAction.vocabulary
            "RegistrationGroup"
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Amendment.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Update)
                    Amendment.Update
                    (function
                    | Amendment.Update value -> Some value
                    | _ -> None)
                    (record id |> field "values" id registrationUpdate)
            ]

    let decisionGroup =
        tagged
            CorrectionGroupAction.vocabulary
            "DecisionGroup"
            [
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Keep) Change.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Update)
                    Change.Update
                    (function
                    | Change.Update value -> Some value
                    | _ -> None)
                    (record id |> field "values" id decisionUpdate)
                unitVariant (CorrectionGroupAction.token CorrectionGroupAction.Clear) Change.Clear
            ]

    let paymentGroup =
        tagged
            CorrectionGroupAction.vocabulary
            "PaymentGroup"
            [
                unitVariant
                    (CorrectionGroupAction.token CorrectionGroupAction.Keep)
                    PaymentChange.Keep
                variant
                    (CorrectionGroupAction.token CorrectionGroupAction.Replace)
                    PaymentChange.Replace
                    (function
                    | PaymentChange.Replace value -> Some value
                    | _ -> None)
                    (record id |> field "values" id payment)
                unitVariant "REAFFIRM" PaymentChange.Reaffirm
                unitVariant
                    (CorrectionGroupAction.token CorrectionGroupAction.Clear)
                    PaymentChange.Clear
            ]

    let correction =
        (record (fun v0 v1 v2 ->
            {
                AuthoredCorrection.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<AuthoredCorrection, _>)
        |> field (RecordGroup.token RecordGroup.Registration) _.Registration (registrationGroup)
        |> field (RecordGroup.token RecordGroup.Decision) _.Decision (decisionGroup)
        |> field (RecordGroup.token RecordGroup.Payment) _.Payment (paymentGroup)
        |> build
        |> named "Correction"

    let private caseCodec (registration, decision, payment) (reason, correction) =
        CaseActKind.all
        |> List.map (fun kind ->
            let tag = CaseActKind.token kind

            let memberCodec name codec (inject, project) =
                variant tag inject project (record id |> field name id codec)

            match CaseAct.slot kind with
            | CaseActSlot.Bare act -> unitVariant tag act
            | CaseActSlot.Registration(inject, project) ->
                memberCodec "values" registration (inject, project)
            | CaseActSlot.RegistrationUpdate(inject, project) ->
                memberCodec "values" registrationUpdate (inject, project)
            | CaseActSlot.Decision(inject, project) ->
                memberCodec "values" decision (inject, project)
            | CaseActSlot.Payment(inject, project) ->
                memberCodec "values" payment (inject, project)
            | CaseActSlot.Reason(inject, project) -> memberCodec "reason" reason (inject, project)
            | CaseActSlot.Correction(inject, project) ->
                memberCodec "groups" correction (inject, project))
        |> tagged CaseActKind.vocabulary "CaseAct"

    let caseAct = caseCodec (registration, decision, payment) (text, correction)

    let principal =
        (record (fun v0 v1 v2 ->
            {
                PrincipalInput.Kind = v0
                Issuer = v1
                Subject = v2
            })
        : Fields<PrincipalInput, _>)
        |> field "kind" _.Kind (VocabularyCodec.create PrincipalKind.vocabulary)
        |> field "issuer" _.Issuer (text)
        |> field "subject" _.Subject (text)
        |> build
        |> named "PrincipalInput"

    let actorProfileInput =
        (record (fun name contact ->
            {
                ActorProfileInput.DisplayName = name
                Contact = Option.flatten contact
            })
        : Fields<ActorProfileInput, _>)
        |> field "displayName" _.DisplayName text
        |> optionalField
            "contact"
            (fun profile -> profile.Contact |> Option.map Some)
            (nullable text)
        |> build
        |> named "ActorProfileInput"

    let private authorityVariantsWith role scope =
        let grant =
            (record (fun role scope ->
                {
                    AuthoredGrant.Role = role
                    Scope = scope
                })
            : Fields<AuthoredGrant, _>)
            |> field "role" _.Role role
            |> field "scope" _.Scope scope

        let grantRow = grant |> build |> named "AuthorityGrantInput"

        let grants =
            Codec.list AuthorityAdmission.MaximumGrants grantRow
            |> constrained
                (Schema.Array(grantRow.Schema, 1, AuthorityAdmission.MaximumGrants))
                (List.isEmpty >> not)

        [
            variant
                (AuthorityKind.token AuthorityKind.Admit)
                AuthorityAct.Admit
                (function
                | AuthorityAct.Admit value -> Some value
                | _ -> None)
                ((record (fun profile grants ->
                    {
                        AuthorityAdmission.Profile = profile
                        Grants = grants
                    })
                 : Fields<AuthorityAdmission<AuthoredGrant, ActorProfileInput>, _>)
                 |> field "profile" _.Profile actorProfileInput
                 |> field "grants" _.Grants grants)
            variant
                (AuthorityKind.token AuthorityKind.Register)
                AuthorityAct.Register
                (function
                | AuthorityAct.Register profile -> Some profile
                | _ -> None)
                (record id |> field "profile" id actorProfileInput)
            variant
                (AuthorityKind.token AuthorityKind.SetProfile)
                AuthorityAct.SetProfile
                (function
                | AuthorityAct.SetProfile profile -> Some profile
                | _ -> None)
                (record id |> field "profile" id actorProfileInput)
            unitVariant (AuthorityKind.token AuthorityKind.EraseProfile) AuthorityAct.EraseProfile
            variant
                (AuthorityKind.token AuthorityKind.Grant)
                AuthorityAct.Grant
                (function
                | AuthorityAct.Grant value -> Some value
                | _ -> None)
                grant
            variant
                (AuthorityKind.token AuthorityKind.Revoke)
                AuthorityAct.Revoke
                (function
                | AuthorityAct.Revoke value -> Some value
                | _ -> None)
                grant
            unitVariant (AuthorityKind.token AuthorityKind.Enable) AuthorityAct.Enable
            unitVariant (AuthorityKind.token AuthorityKind.Disable) AuthorityAct.Disable
        ]

    let authorityActWith role scope =
        authorityVariantsWith role scope
        |> tagged AuthorityKind.vocabulary "AuthorityAct"
