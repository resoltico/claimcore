namespace ClaimCore.Domain

open System

[<RequireQualifiedAccess>]
type CorrectionGroupAction =
    | Keep
    | Replace
    | Clear

/// Closed operation vocabulary; names and payload kinds belong to the core, not a renderer.
[<RequireQualifiedAccess>]
type CaseActKind =
    | Open
    | AmendRegistration
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

[<RequireQualifiedAccess>]
type ActPayload =
    | Bare of BareAct
    | Registration
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

    let private description family payload revision capabilities =
        {
            Family = family
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
        | CaseActKind.AmendRegistration ->
            description
                ActFamily.Business
                ActPayload.Registration
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
    | Replace of 'value

[<RequireQualifiedAccess>]
type Change<'value> =
    | Keep
    | Replace of 'value
    | Clear

type Correction<'registration, 'decision, 'payment> =
    {
        Registration: Amendment<'registration>
        Decision: Change<'decision>
        Payment: Change<'payment>
    }

[<RequireQualifiedAccess>]
type CaseAct<'registration, 'decision, 'payment, 'reason> =
    | Bare of BareAct
    | Open of 'registration
    | AmendRegistration of 'registration
    | Correct of Correction<'registration, 'decision, 'payment>
    | Decide of 'decision
    | RecordPayment of 'payment
    | Void of 'reason
    | Reinstate of 'reason

type AuthoredCaseAct = CaseAct<RegistrationInput, DecisionInput, PaymentInput, string>
type AuthoredCorrection = Correction<RegistrationInput, DecisionInput, PaymentInput>
type ValidCaseAct = CaseAct<Registration, Decision, Payment, Reason>
type ValidCorrection = Correction<Registration, Decision, Payment>

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type CaseActSlot<'registration, 'decision, 'payment, 'reason> =
    | Bare of CaseAct<'registration, 'decision, 'payment, 'reason>
    | Registration of
        inject: ('registration -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project: (CaseAct<'registration, 'decision, 'payment, 'reason> -> 'registration option)
    | Decision of
        inject: ('decision -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project: (CaseAct<'registration, 'decision, 'payment, 'reason> -> 'decision option)
    | Payment of
        inject: ('payment -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project: (CaseAct<'registration, 'decision, 'payment, 'reason> -> 'payment option)
    | Reason of
        inject: ('reason -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project: (CaseAct<'registration, 'decision, 'payment, 'reason> -> 'reason option)
    | Correction of
        inject:
            (Correction<'registration, 'decision, 'payment>
                -> CaseAct<'registration, 'decision, 'payment, 'reason>) *
        project:
            (CaseAct<'registration, 'decision, 'payment, 'reason>
                -> Correction<'registration, 'decision, 'payment> option)

module CaseAct =
    let kind =
        function
        | CaseAct.Bare bare ->
            CaseActKind.all
            |> List.find (fun kind -> (CaseActKind.describe kind).Payload = ActPayload.Bare bare)
        | CaseAct.Open _ -> CaseActKind.Open
        | CaseAct.AmendRegistration _ -> CaseActKind.AmendRegistration
        | CaseAct.Correct _ -> CaseActKind.Correct
        | CaseAct.Decide _ -> CaseActKind.Decide
        | CaseAct.RecordPayment _ -> CaseActKind.RecordPayment
        | CaseAct.Void _ -> CaseActKind.Void
        | CaseAct.Reinstate _ -> CaseActKind.Reinstate

    let private amend transform =
        function
        | Amendment.Keep -> Ok Amendment.Keep
        | Amendment.Replace value -> transform value |> Result.map Amendment.Replace

    let private change transform =
        function
        | Change.Keep -> Ok Change.Keep
        | Change.Clear -> Ok Change.Clear
        | Change.Replace value -> transform value |> Result.map Change.Replace

    let private correction registration decision payment value =
        amend registration value.Registration
        |> Result.bind (fun r ->
            change decision value.Decision
            |> Result.bind (fun d ->
                change payment value.Payment
                |> Result.map (fun p ->
                    CaseAct.Correct
                        {
                            Registration = r
                            Decision = d
                            Payment = p
                        })))

    let traverse registration decision payment reason =
        function
        | CaseAct.Bare bare -> Ok(CaseAct.Bare bare)
        | CaseAct.Open value -> registration value |> Result.map CaseAct.Open
        | CaseAct.AmendRegistration value ->
            registration value |> Result.map CaseAct.AmendRegistration
        | CaseAct.Decide value -> decision value |> Result.map CaseAct.Decide
        | CaseAct.RecordPayment value -> payment value |> Result.map CaseAct.RecordPayment
        | CaseAct.Void value -> reason value |> Result.map CaseAct.Void
        | CaseAct.Reinstate value -> reason value |> Result.map CaseAct.Reinstate
        | CaseAct.Correct value -> correction registration decision payment value

    let map registration decision payment reason act =
        traverse (registration >> Ok) (decision >> Ok) (payment >> Ok) (reason >> Ok) act
        |> Result.defaultWith (fun () -> invalidOp "A total act mapping cannot fail.")

    let private projectOpen =
        function
        | CaseAct.Open value -> Some value
        | _ -> None

    let private projectAmendRegistration =
        function
        | CaseAct.AmendRegistration value -> Some value
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

    let private registrationSlot =
        function
        | CaseActKind.Open -> CaseActSlot.Registration(CaseAct.Open, projectOpen)
        | CaseActKind.AmendRegistration ->
            CaseActSlot.Registration(CaseAct.AmendRegistration, projectAmendRegistration)
        | _ -> invalidOp "A registration description requires a registration act."

    let private reasonSlot =
        function
        | CaseActKind.Void -> CaseActSlot.Reason(CaseAct.Void, projectVoid)
        | CaseActKind.Reinstate -> CaseActSlot.Reason(CaseAct.Reinstate, projectReinstate)
        | _ -> invalidOp "A reason description requires a reason act."

    let slot<'registration, 'decision, 'payment, 'reason>
        kind
        : CaseActSlot<'registration, 'decision, 'payment, 'reason> =
        match (CaseActKind.describe kind).Payload with
        | ActPayload.Bare bare -> CaseActSlot.Bare(CaseAct.Bare bare)
        | ActPayload.Registration -> registrationSlot kind
        | ActPayload.Decision -> CaseActSlot.Decision(CaseAct.Decide, projectDecide)
        | ActPayload.Payment -> CaseActSlot.Payment(CaseAct.RecordPayment, projectRecordPayment)
        | ActPayload.Reason -> reasonSlot kind
        | ActPayload.Correction -> CaseActSlot.Correction(CaseAct.Correct, projectCorrect)

    let probe kind : CaseAct<unit, unit, unit, unit> =
        match slot kind with
        | CaseActSlot.Bare act -> act
        | CaseActSlot.Registration(inject, _)
        | CaseActSlot.Decision(inject, _)
        | CaseActSlot.Payment(inject, _)
        | CaseActSlot.Reason(inject, _) -> inject ()
        | CaseActSlot.Correction(inject, _) ->
            inject
                {
                    Registration = Amendment.Keep
                    Decision = Change.Keep
                    Payment = Change.Keep
                }

    let registration = Registration.authored
    let decision = Decision.authored
    let private payment = Payment.authored

    let authored (act: ValidCaseAct) : AuthoredCaseAct =
        map registration decision payment Reason.format act

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

module Amendment =
    let private action =
        function
        | Amendment.Keep -> CorrectionGroupAction.Keep
        | Amendment.Replace _ -> CorrectionGroupAction.Replace

    let actions =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<Amendment<unit>>
        |> Array.map (fun case ->
            let payloads = Array.create (case.GetFields().Length) (box ())

            (Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, payloads) |> nonNull)
            :?> Amendment<unit>
            |> action)
        |> Array.toList
