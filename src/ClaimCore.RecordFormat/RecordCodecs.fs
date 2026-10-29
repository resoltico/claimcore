namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain
open ClaimCore.Json


module internal RecordCodecs =
    open Codec

    let private tagged name variants =
        Codec.taggedOn DurableNames.Kind name variants

    let revision = integer 0L JsonValue.MaximumInteger
    let sequence = integer 1L JsonValue.MaximumInteger

    let private inputGroup group create values =
        CaseFields.ofGroup group
        |> List.map (fun field -> field, DurableNames.field field, Codec.text)
        |> Codec.fieldMap
        |> Codec.mapResult
            (fun path fields ->
                create (Map.toList fields)
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> Map.ofList)

    let registration =
        inputGroup RecordGroup.Registration RegistrationInput.create RegistrationInput.values
        |> Codec.named "Registration"

    let decision =
        inputGroup RecordGroup.Decision DecisionInput.create DecisionInput.values
        |> Codec.named "Decision"

    let payment =
        inputGroup RecordGroup.Payment PaymentInput.create PaymentInput.values
        |> Codec.named "Payment"

    let registrationGroup =
        tagged
            "RegistrationGroup"
            [
                unitVariant (DurableNames.correction CorrectionGroupAction.Keep) Amendment.Keep
                variant
                    (DurableNames.correction CorrectionGroupAction.Replace)
                    Amendment.Replace
                    (function
                    | Amendment.Replace value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.Values id registration)
            ]

    let decisionGroup =
        tagged
            "DecisionGroup"
            [
                unitVariant (DurableNames.correction CorrectionGroupAction.Keep) Change.Keep
                variant
                    (DurableNames.correction CorrectionGroupAction.Replace)
                    Change.Replace
                    (function
                    | Change.Replace value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.Values id decision)
                unitVariant (DurableNames.correction CorrectionGroupAction.Clear) Change.Clear
            ]

    let paymentGroup =
        tagged
            "PaymentGroup"
            [
                unitVariant (DurableNames.correction CorrectionGroupAction.Keep) Change.Keep
                variant
                    (DurableNames.correction CorrectionGroupAction.Replace)
                    Change.Replace
                    (function
                    | Change.Replace value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.Values id payment)
                unitVariant (DurableNames.correction CorrectionGroupAction.Clear) Change.Clear
            ]

    let correction =
        (record (fun v0 v1 v2 ->
            {
                AuthoredCorrection.Registration = v0
                Decision = v1
                Payment = v2
            })
        : Fields<AuthoredCorrection, _>)
        |> field DurableNames.Registration _.Registration (registrationGroup)
        |> field DurableNames.Decision _.Decision (decisionGroup)
        |> field DurableNames.Payment _.Payment (paymentGroup)
        |> build
        |> named "Correction"

    let private caseCodec (registration, decision, payment) (reason, correction) =
        DurableNames.caseKinds
        |> List.map (fun kind ->
            let tag = DurableNames.caseAct kind

            let memberCodec name codec (inject, project) =
                variant tag inject project (record id |> field name id codec)

            match CaseAct.slot kind with
            | CaseActSlot.Bare act -> unitVariant tag act
            | CaseActSlot.Registration(inject, project) ->
                memberCodec DurableNames.Values registration (inject, project)
            | CaseActSlot.Decision(inject, project) ->
                memberCodec DurableNames.Values decision (inject, project)
            | CaseActSlot.Payment(inject, project) ->
                memberCodec DurableNames.Values payment (inject, project)
            | CaseActSlot.Reason(inject, project) ->
                memberCodec DurableNames.Reason reason (inject, project)
            | CaseActSlot.Correction(inject, project) ->
                memberCodec DurableNames.Groups correction (inject, project))
        |> tagged "CaseAct"

    let caseAct = caseCodec (registration, decision, payment) (text, correction)

    let caseRequestFields =
        (record (fun v0 v1 v2 v3 () ->
            {
                UnvalidatedCaseRequest.OperationId = v0
                CaseReference = v1
                ExpectedRevision = v2
                Act = v3
            })
        : Fields<UnvalidatedCaseRequest, _>)
        |> field DurableNames.OperationId _.OperationId (uuid)
        |> field DurableNames.CaseReference _.CaseReference (text)
        |> field DurableNames.ExpectedRevision _.ExpectedRevision (revision)
        |> field DurableNames.Act _.Act (caseAct)

    let principal =
        (record (fun v0 v1 v2 ->
            {
                PrincipalInput.Kind = v0
                Issuer = v1
                Subject = v2
            })
        : Fields<PrincipalInput, _>)
        |> field DurableNames.Kind _.Kind (enumeration DurableNames.principalKindEntries)
        |> field DurableNames.Issuer _.Issuer (text)
        |> field DurableNames.Subject _.Subject (text)
        |> build
        |> named "Principal"

    let scope =
        tagged
            "Scope"
            [
                unitVariant DurableNames.InstallationScope GrantTarget.Installation
                variant
                    DurableNames.CaseScope
                    GrantTarget.CaseReference
                    (function
                    | GrantTarget.CaseReference value -> Some value
                    | _ -> None)
                    (record id |> field DurableNames.CaseReference id text)
            ]

    let private authorityVariantsWith role scope =
        let grant =
            record (fun role scope ->
                {
                    AuthoredGrant.Role = role
                    Scope = scope
                })
            |> field DurableNames.Role _.Role role
            |> field DurableNames.Scope _.Scope scope

        DurableNames.authorityKinds
        |> List.filter ((<>) AuthorityKind.ProvisionOwner)
        |> List.map (fun kind ->
            let token = DurableNames.authorityAct kind

            match AuthorityAct.slot kind with
            | AuthorityActSlot.Bare act -> unitVariant token act
            | AuthorityActSlot.Grant(inject, project) -> variant token inject project grant)

    let role = enumeration DurableNames.roleEntries
    let authorityVariants role = authorityVariantsWith role scope

    let caseOperation =
        let version = int64 RecordVersions.CaseRequest

        let fields =
            caseRequestFields
            |> field
                DurableNames.Format
                (fun _ -> ())
                (integer version version |> map (fun _ -> ()) (fun () -> version))

        tagged "CaseRequestRecord.Subject" [ variant DurableNames.CaseSubject id Some fields ]

    let private scalar parse format =
        text
        |> mapResult
            (fun path raw ->
                parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid accepted scalar format."
                    }))
            format

    let private acceptedReference = scalar CaseReference.parse CaseReference.format

    let private acceptedGroup group create values =
        CaseFields.declarations
        |> List.filter (fun row -> row.Group = Some group)
        |> List.map (fun row ->
            row.Field,
            DurableNames.field row.Field,
            scalar (FieldValue.parse row.Kind) FieldValue.format)
        |> Codec.fieldMap
        |> mapResult
            (fun path fields ->
                create (Map.toList fields)
                |> Result.mapError (fun reason -> { Path = path; Reason = reason }))
            (values >> Map.ofList)

    let private acceptedRegistration =
        acceptedGroup RecordGroup.Registration Registration.create Registration.values
        |> named "Registration"

    let private acceptedDecision =
        acceptedGroup RecordGroup.Decision Decision.create Decision.values
        |> named "Decision"

    let private acceptedPayment =
        acceptedGroup RecordGroup.Payment Payment.create Payment.values
        |> named "Payment"

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
        |> field DurableNames.CaseReference _.CaseReference acceptedReference
        |> field DurableNames.Status _.Status (enumeration (DurableNames.statusEntries))
        |> field DurableNames.Registration _.Registration acceptedRegistration
        |> field DurableNames.Decision _.Decision (nullable acceptedDecision)
        |> field DurableNames.Payment _.Payment (nullable acceptedPayment)
        |> buildResult (fun path () ->
            {
                Path = path + ".payment"
                Reason = "Payment requires a decision."
            })

    let liveState =
        let fields = record id |> field DurableNames.Fields id caseFields

        tagged
            "SnapshotRecord.State"
            [
                variant
                    (DurableNames.lifecycle Lifecycle.Active)
                    LiveCase.Active
                    (function
                    | LiveCase.Active fields -> Some fields
                    | _ -> None)
                    fields
                variant
                    (DurableNames.lifecycle Lifecycle.Voided)
                    LiveCase.Voided
                    (function
                    | LiveCase.Voided fields -> Some fields
                    | _ -> None)
                    fields
            ]
