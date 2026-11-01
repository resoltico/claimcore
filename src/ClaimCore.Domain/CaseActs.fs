namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type CorrectionGroupAction =
    | Keep
    | Replace
    | Update
    | Clear
    | Reaffirm

/// Closed operation vocabulary; names and payload kinds belong to the core, not a renderer.
[<RequireQualifiedAccess>]
type CaseActKind =
    | Open
    | AmendRegistration
    | UpdateWork
    | Correct
    | Decide
    | WithdrawDecision
    | RecordPayment
    | ClearPayment
    | Close
    | Reopen
    | Void
    | Reinstate
    | Erase

[<RequireQualifiedAccess>]
type BareAct =
    | WithdrawDecision
    | ClearPayment
    | Close
    | Reopen
    | Erase

[<RequireQualifiedAccess>]
type ActFamily =
    | Business
    | Stewardship

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActFamily =
    let vocabulary =
        Vocabulary.ofUnion<ActFamily> "actFamily" TokenCasing.ScreamingSnake None

[<RequireQualifiedAccess>]
type CaseEffect =
    | RecordFacts
    | UpdateWork
    | ChangeStatus
    | ChangeLifecycle
    | ErasePayload

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseEffect =
    let vocabulary =
        Vocabulary.ofUnion<CaseEffect> "caseEffect" TokenCasing.ScreamingSnake None

    let token = Vocabulary.token vocabulary

[<RequireQualifiedAccess>]
type ActPayload =
    | Bare of BareAct
    | Registration
    | RegistrationUpdate
    | Work
    | Decision
    | Payment
    | Reason
    | Correction

[<RequireQualifiedAccess>]
type RevisionInput =
    | New
    | Expected

[<RequireQualifiedAccess>]
type ActCapabilities =
    | Required of Capability list
    | CorrectionGroups

type CaseActDescription =
    {
        Family: ActFamily
        Effect: CaseEffect
        AffectedSections: CaseSection list
        Payload: ActPayload
        Revision: RevisionInput
        Capabilities: ActCapabilities
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CaseActKind =
    let vocabulary =
        Vocabulary.ofUnion<CaseActKind> "act" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary

    let private effect =
        function
        | ActPayload.Bare(BareAct.Close | BareAct.Reopen) -> CaseEffect.ChangeStatus
        | ActPayload.Bare BareAct.Erase -> CaseEffect.ErasePayload
        | ActPayload.Reason -> CaseEffect.ChangeLifecycle
        | ActPayload.Work -> CaseEffect.UpdateWork
        | ActPayload.Bare(BareAct.WithdrawDecision | BareAct.ClearPayment)
        | ActPayload.Registration
        | ActPayload.RegistrationUpdate
        | ActPayload.Decision
        | ActPayload.Payment
        | ActPayload.Correction -> CaseEffect.RecordFacts

    let private sections =
        function
        | ActPayload.Registration
        | ActPayload.RegistrationUpdate -> [ CaseSection.Registration ]
        | ActPayload.Decision
        | ActPayload.Bare BareAct.WithdrawDecision -> [ CaseSection.Decision ]
        | ActPayload.Payment
        | ActPayload.Bare BareAct.ClearPayment -> [ CaseSection.Payment ]
        | ActPayload.Work -> [ CaseSection.Work ]
        | ActPayload.Correction ->
            [ CaseSection.Registration; CaseSection.Decision; CaseSection.Payment ]
        | ActPayload.Bare BareAct.Erase ->
            [
                CaseSection.Registration
                CaseSection.Decision
                CaseSection.Payment
                CaseSection.Work
            ]
        | ActPayload.Reason
        | ActPayload.Bare(BareAct.Close | BareAct.Reopen) -> []

    let private description family payload revision capabilities =
        {
            Family = family
            Effect = effect payload
            AffectedSections = sections payload
            Payload = payload
            Revision = revision
            Capabilities = capabilities
        }

    // One exhaustive table owns the facts of every closed case-act kind.
    // fsharplint:disable-next-line CyclomaticComplexity
    let describe =
        function
        | CaseActKind.Open ->
            description
                ActFamily.Business
                ActPayload.Registration
                RevisionInput.New
                (ActCapabilities.Required [ Capability.Register ])
        | CaseActKind.UpdateWork ->
            description
                ActFamily.Business
                ActPayload.Work
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.ReadCase; Capability.ManageCaseWork ])
        | CaseActKind.AmendRegistration ->
            description
                ActFamily.Business
                ActPayload.RegistrationUpdate
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.Register ])
        | CaseActKind.Correct ->
            description
                ActFamily.Business
                ActPayload.Correction
                RevisionInput.Expected
                ActCapabilities.CorrectionGroups
        | CaseActKind.Decide ->
            description
                ActFamily.Business
                ActPayload.Decision
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.Decide ])
        | CaseActKind.WithdrawDecision ->
            description
                ActFamily.Business
                (ActPayload.Bare BareAct.WithdrawDecision)
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.Decide ])
        | CaseActKind.RecordPayment ->
            description
                ActFamily.Business
                ActPayload.Payment
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.RecordPayment ])
        | CaseActKind.ClearPayment ->
            description
                ActFamily.Business
                (ActPayload.Bare BareAct.ClearPayment)
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.RecordPayment ])
        | CaseActKind.Close ->
            description
                ActFamily.Business
                (ActPayload.Bare BareAct.Close)
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.Close ])
        | CaseActKind.Reopen ->
            description
                ActFamily.Business
                (ActPayload.Bare BareAct.Reopen)
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.Close ])
        | CaseActKind.Void ->
            description
                ActFamily.Stewardship
                ActPayload.Reason
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.Steward ])
        | CaseActKind.Reinstate ->
            description
                ActFamily.Stewardship
                ActPayload.Reason
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.Steward ])
        | CaseActKind.Erase ->
            description
                ActFamily.Stewardship
                (ActPayload.Bare BareAct.Erase)
                RevisionInput.Expected
                (ActCapabilities.Required [ Capability.Steward ])

[<RequireQualifiedAccess>]
type Amendment<'value> =
    | Keep
    | Update of 'value

[<RequireQualifiedAccess>]
type Change<'value> =
    | Keep
    | Update of 'value
    | Clear

[<RequireQualifiedAccess>]
type PaymentChange<'value> =
    | Keep
    | Replace of 'value
    | Reaffirm
    | Clear

type Correction<'registration, 'decision, 'payment> =
    {
        Registration: Amendment<'registration>
        Decision: Change<'decision>
        Payment: PaymentChange<'payment>
    }

[<RequireQualifiedAccess>]
type CaseAct<'registration, 'registrationUpdate, 'decision, 'decisionUpdate, 'payment, 'work, 'reason>
    =
    | Bare of BareAct
    | Open of 'registration
    | AmendRegistration of 'registrationUpdate
    | UpdateWork of 'work
    | Correct of Correction<'registrationUpdate, 'decisionUpdate, 'payment>
    | Decide of 'decision
    | RecordPayment of 'payment
    | Void of 'reason
    | Reinstate of 'reason

type AuthoredCaseAct =
    CaseAct<
        RegistrationInput,
        RegistrationUpdateInput,
        DecisionInput,
        DecisionUpdateInput,
        PaymentInput,
        CaseWorkInput,
        string
     >

type AuthoredCorrection = Correction<RegistrationUpdateInput, DecisionUpdateInput, PaymentInput>

type ValidCaseAct =
    CaseAct<
        Registration,
        ValidRegistrationUpdate,
        Decision,
        ValidDecisionUpdate,
        Payment,
        ValidCaseWorkUpdate,
        Reason
     >

type ValidCorrection = Correction<ValidRegistrationUpdate, ValidDecisionUpdate, Payment>

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type CaseActSlot<'registration, 'registrationUpdate, 'decision, 'decisionUpdate, 'payment, 'work, 'reason>
    =
    | Bare of
        CaseAct<
            'registration,
            'registrationUpdate,
            'decision,
            'decisionUpdate,
            'payment,
            'work,
            'reason
         >
    | Registration of
        inject:
            ('registration
                -> CaseAct<
                    'registration,
                    'registrationUpdate,
                    'decision,
                    'decisionUpdate,
                    'payment,
                    'work,
                    'reason
                    >) *
        project:
            (CaseAct<
                'registration,
                'registrationUpdate,
                'decision,
                'decisionUpdate,
                'payment,
                'work,
                'reason
              >
                -> 'registration option)
    | RegistrationUpdate of
        inject:
            ('registrationUpdate
                -> CaseAct<
                    'registration,
                    'registrationUpdate,
                    'decision,
                    'decisionUpdate,
                    'payment,
                    'work,
                    'reason
                    >) *
        project:
            (CaseAct<
                'registration,
                'registrationUpdate,
                'decision,
                'decisionUpdate,
                'payment,
                'work,
                'reason
              >
                -> 'registrationUpdate option)
    | Work of
        inject:
            ('work
                -> CaseAct<
                    'registration,
                    'registrationUpdate,
                    'decision,
                    'decisionUpdate,
                    'payment,
                    'work,
                    'reason
                    >) *
        project:
            (CaseAct<
                'registration,
                'registrationUpdate,
                'decision,
                'decisionUpdate,
                'payment,
                'work,
                'reason
              >
                -> 'work option)
    | Decision of
        inject:
            ('decision
                -> CaseAct<
                    'registration,
                    'registrationUpdate,
                    'decision,
                    'decisionUpdate,
                    'payment,
                    'work,
                    'reason
                    >) *
        project:
            (CaseAct<
                'registration,
                'registrationUpdate,
                'decision,
                'decisionUpdate,
                'payment,
                'work,
                'reason
              >
                -> 'decision option)
    | Payment of
        inject:
            ('payment
                -> CaseAct<
                    'registration,
                    'registrationUpdate,
                    'decision,
                    'decisionUpdate,
                    'payment,
                    'work,
                    'reason
                    >) *
        project:
            (CaseAct<
                'registration,
                'registrationUpdate,
                'decision,
                'decisionUpdate,
                'payment,
                'work,
                'reason
              >
                -> 'payment option)
    | Reason of
        inject:
            ('reason
                -> CaseAct<
                    'registration,
                    'registrationUpdate,
                    'decision,
                    'decisionUpdate,
                    'payment,
                    'work,
                    'reason
                    >) *
        project:
            (CaseAct<
                'registration,
                'registrationUpdate,
                'decision,
                'decisionUpdate,
                'payment,
                'work,
                'reason
              >
                -> 'reason option)
    | Correction of
        inject:
            (Correction<'registrationUpdate, 'decisionUpdate, 'payment>
                -> CaseAct<
                    'registration,
                    'registrationUpdate,
                    'decision,
                    'decisionUpdate,
                    'payment,
                    'work,
                    'reason
                    >) *
        project:
            (CaseAct<
                'registration,
                'registrationUpdate,
                'decision,
                'decisionUpdate,
                'payment,
                'work,
                'reason
              >
                -> Correction<'registrationUpdate, 'decisionUpdate, 'payment> option)

module CaseAct =
    let kind =
        function
        | CaseAct.Bare bare ->
            CaseActKind.all
            |> List.find (fun kind -> (CaseActKind.describe kind).Payload = ActPayload.Bare bare)
        | CaseAct.Open _ -> CaseActKind.Open
        | CaseAct.AmendRegistration _ -> CaseActKind.AmendRegistration
        | CaseAct.UpdateWork _ -> CaseActKind.UpdateWork
        | CaseAct.Correct _ -> CaseActKind.Correct
        | CaseAct.Decide _ -> CaseActKind.Decide
        | CaseAct.RecordPayment _ -> CaseActKind.RecordPayment
        | CaseAct.Void _ -> CaseActKind.Void
        | CaseAct.Reinstate _ -> CaseActKind.Reinstate

    let private amend transform =
        function
        | Amendment.Keep -> Ok Amendment.Keep
        | Amendment.Update value -> transform value |> Result.map Amendment.Update

    let private change transform =
        function
        | Change.Keep -> Ok Change.Keep
        | Change.Clear -> Ok Change.Clear
        | Change.Update value -> transform value |> Result.map Change.Update

    let private paymentChange transform =
        function
        | PaymentChange.Keep -> Ok PaymentChange.Keep
        | PaymentChange.Clear -> Ok PaymentChange.Clear
        | PaymentChange.Reaffirm -> Ok PaymentChange.Reaffirm
        | PaymentChange.Replace value -> transform value |> Result.map PaymentChange.Replace

    let private correction registration decision payment value =
        amend registration value.Registration
        |> Result.bind (fun r ->
            change decision value.Decision
            |> Result.bind (fun d ->
                paymentChange payment value.Payment
                |> Result.map (fun p ->
                    CaseAct.Correct
                        {
                            Registration = r
                            Decision = d
                            Payment = p
                        })))

    let traverse registration registrationUpdate decision decisionUpdate payment work reason =
        function
        | CaseAct.Bare bare -> Ok(CaseAct.Bare bare)
        | CaseAct.Open value -> registration value |> Result.map CaseAct.Open
        | CaseAct.AmendRegistration value ->
            registrationUpdate value |> Result.map CaseAct.AmendRegistration
        | CaseAct.UpdateWork value -> work value |> Result.map CaseAct.UpdateWork
        | CaseAct.Decide value -> decision value |> Result.map CaseAct.Decide
        | CaseAct.RecordPayment value -> payment value |> Result.map CaseAct.RecordPayment
        | CaseAct.Void value -> reason value |> Result.map CaseAct.Void
        | CaseAct.Reinstate value -> reason value |> Result.map CaseAct.Reinstate
        | CaseAct.Correct value -> correction registrationUpdate decisionUpdate payment value

    let map registration registrationUpdate decision decisionUpdate payment work reason act =
        traverse
            (registration >> Ok)
            (registrationUpdate >> Ok)
            (decision >> Ok)
            (decisionUpdate >> Ok)
            (payment >> Ok)
            (work >> Ok)
            (reason >> Ok)
            act
        |> Result.defaultWith (fun () -> invalidOp "A total act mapping cannot fail.")

    let private projectOpen =
        function
        | CaseAct.Open value -> Some value
        | _ -> None

    let private projectAmendRegistration =
        function
        | CaseAct.AmendRegistration value -> Some value
        | _ -> None

    let private projectWork =
        function
        | CaseAct.UpdateWork value -> Some value
        | _ -> None

    let private projectCorrect =
        function
        | CaseAct.Correct value -> Some value
        | _ -> None

    let private projectDecide =
        function
        | CaseAct.Decide value -> Some value
        | _ -> None

    let private projectRecordPayment =
        function
        | CaseAct.RecordPayment value -> Some value
        | _ -> None

    let private projectVoid =
        function
        | CaseAct.Void value -> Some value
        | _ -> None

    let private projectReinstate =
        function
        | CaseAct.Reinstate value -> Some value
        | _ -> None

    let private reasonSlot =
        function
        | CaseActKind.Void -> CaseActSlot.Reason(CaseAct.Void, projectVoid)
        | CaseActKind.Reinstate -> CaseActSlot.Reason(CaseAct.Reinstate, projectReinstate)
        | _ -> invalidOp "A reason description requires a reason act."

    let slot<'registration, 'registrationUpdate, 'decision, 'decisionUpdate, 'payment, 'work, 'reason>
        kind
        : CaseActSlot<
            'registration,
            'registrationUpdate,
            'decision,
            'decisionUpdate,
            'payment,
            'work,
            'reason
           >
        =
        match (CaseActKind.describe kind).Payload with
        | ActPayload.Bare bare -> CaseActSlot.Bare(CaseAct.Bare bare)
        | ActPayload.Registration -> CaseActSlot.Registration(CaseAct.Open, projectOpen)
        | ActPayload.RegistrationUpdate ->
            CaseActSlot.RegistrationUpdate(CaseAct.AmendRegistration, projectAmendRegistration)
        | ActPayload.Work -> CaseActSlot.Work(CaseAct.UpdateWork, projectWork)
        | ActPayload.Decision -> CaseActSlot.Decision(CaseAct.Decide, projectDecide)
        | ActPayload.Payment -> CaseActSlot.Payment(CaseAct.RecordPayment, projectRecordPayment)
        | ActPayload.Reason -> reasonSlot kind
        | ActPayload.Correction -> CaseActSlot.Correction(CaseAct.Correct, projectCorrect)

    let probe kind : CaseAct<unit, unit, unit, unit, unit, unit, unit> =
        match slot kind with
        | CaseActSlot.Bare act -> act
        | CaseActSlot.Registration(inject, _)
        | CaseActSlot.RegistrationUpdate(inject, _)
        | CaseActSlot.Work(inject, _)
        | CaseActSlot.Decision(inject, _)
        | CaseActSlot.Payment(inject, _)
        | CaseActSlot.Reason(inject, _) -> inject ()
        | CaseActSlot.Correction(inject, _) ->
            inject
                {
                    Registration = Amendment.Keep
                    Decision = Change.Keep
                    Payment = PaymentChange.Keep
                }

    let registration = Registration.authored
    let decision = Decision.authored
    let private payment = Payment.authored

    let authored (act: ValidCaseAct) : AuthoredCaseAct =
        map
            registration
            RegistrationUpdate.authored
            decision
            DecisionUpdate.authored
            payment
            CaseWork.authored
            Reason.format
            act

type UnvalidatedCaseRequest =
    {
        OperationId: Guid
        CaseReference: string
        ExpectedRevision: int64
        Act: AuthoredCaseAct
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module CorrectionGroupAction =
    let vocabulary =
        Vocabulary.ofUnion<CorrectionGroupAction> "correction" TokenCasing.ScreamingSnake None

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary

module private CorrectionActions =
    let collect<'choice> action =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<'choice>
        |> Array.map (fun case ->
            let payloads = Array.create (case.GetFields().Length) (box ())

            (Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, payloads) |> nonNull)
            :?> 'choice
            |> action)
        |> Array.toList

module Amendment =
    let actions =
        CorrectionActions.collect<Amendment<unit>> (function
            | Amendment.Keep -> CorrectionGroupAction.Keep
            | Amendment.Update _ -> CorrectionGroupAction.Update)

module Change =
    let actions =
        CorrectionActions.collect<Change<unit>> (function
            | Change.Keep -> CorrectionGroupAction.Keep
            | Change.Update _ -> CorrectionGroupAction.Update
            | Change.Clear -> CorrectionGroupAction.Clear)

module PaymentChange =
    let actions =
        CorrectionActions.collect<PaymentChange<unit>> (function
            | PaymentChange.Keep -> CorrectionGroupAction.Keep
            | PaymentChange.Replace _ -> CorrectionGroupAction.Replace
            | PaymentChange.Reaffirm -> CorrectionGroupAction.Reaffirm
            | PaymentChange.Clear -> CorrectionGroupAction.Clear)
