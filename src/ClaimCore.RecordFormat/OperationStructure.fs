namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain

/// Public here means retained structural metadata, never anonymous API disclosure.
type OperationHeader =
    {
        InstallationId: Guid
        Sequence: Sequence
        PreviousHash: EntryHash
        OperationId: OperationId
        ActorId: ActorId
        RecordedAt: DateTimeOffset
        BusinessDate: DateOnly option
    }

type CaseCommandSkeleton =
    {
        CaseId: CaseId
        Reference: CaseReference
        ExpectedRevision: Revision
        Act: CaseActKind
        SelectedPayment: OperationId option
    }

module CaseCommandSkeleton =
    let compartment (command: CaseCommandSkeleton) =
        if command.Act = CaseActKind.Erase then None else Some 0

type AuthorityCommandSkeleton =
    {
        TargetActorId: ActorId
        Principal: PrincipalKey
        // Profile values are replaced by a compartment ordinal, not a dummy private profile.
        Act: AuthorityAct<ActorGrant, int>
    }

type ReminderCommandSkeleton =
    {
        CaseId: CaseId
        Act: ReminderActKind
        ReminderId: ReminderId option
        ExpectedRevision: Revision option
    }

[<RequireQualifiedAccess>]
type NoteCommandSkeleton =
    | Add of CaseId
    | Remove of CaseId * NoteId

[<RequireQualifiedAccess>]
type CommandSkeleton =
    | Case of CaseCommandSkeleton
    | Authority of AuthorityCommandSkeleton
    | Work of CaseId * Revision
    | Note of NoteCommandSkeleton
    | Reminder of ReminderCommandSkeleton

[<RequireQualifiedAccess>]
type PaymentReference =
    | Recorded of OperationId
    | Removed of OperationId

[<RequireQualifiedAccess>]
type AuthorityEffectDescriptor =
    | ProvisionOwner of PrincipalKey
    | Register of PrincipalKey
    | SetProfile of compartmentOrdinal: int
    | EraseProfile
    | Grant of ActorGrant
    | Revoke of ActorGrant
    | Enable
    | Disable

/// No amounts, payment dates, narratives, summaries, responsibility or profile values belong here.
[<RequireQualifiedAccess>]
type EffectDescriptor =
    | Case of
        CaseId *
        Revision *
        CaseActKind *
        creationReference: CaseReference option *
        PaymentReference option
    | Authority of ActorId * AuthorityEffectDescriptor
    | Work of CaseId * Revision
    | NoteAdded of CaseId * NoteId
    | NoteRemoved of CaseId * NoteId
    | ReminderCreated of CaseId * ReminderId * Revision
    | ReminderChanged of CaseId * ReminderId * Revision * ReminderActKind

[<RequireQualifiedAccess>]
type OperationStructureProblem =
    | Header
    | Command
    | Effects
    | Compartments
    | CompartmentBinding
    | UnavailableAppendContent

/// A closed structural operation can have unavailable private values after authorized erasure.
/// Semantic replay and the later erase evidence are separate requirements.
type OperationStructure =
    private
        {
            HeaderValue: OperationHeader
            CommandValue: CommandSkeleton
            EffectsValue: EffectDescriptor list
            CompartmentsValue: Compartment list
        }

    member this.Header = this.HeaderValue
    member this.Command = this.CommandValue
    member this.Effects = this.EffectsValue
    member this.Compartments = this.CompartmentsValue

module OperationStructure =
    // ADMIT has register/profile plus the Domain's bounded grant collection.
    let MaximumEffects = 2 + AuthorityAdmission.MaximumGrants

    let private successor revision =
        if revision < Revision.zero || Revision.value revision >= Revision.Maximum then
            None
        else
            Some(Revision.successor revision)

    let private descriptor owner purpose references =
        CompartmentDescriptor.create 0 owner purpose references
        |> Result.mapError (fun _ -> OperationStructureProblem.Compartments)

    let private caseShape header (command: CaseCommandSkeleton) =
        let opening = command.Act = CaseActKind.Open
        let removing = command.Act = CaseActKind.RemovePayment

        if
            (opening
             && (command.ExpectedRevision <> Revision.zero
                 || command.CaseId <> CaseId.ofOperationId header.OperationId))
            || (not opening && command.ExpectedRevision < Revision.first)
            || (removing <> command.SelectedPayment.IsSome)
        then
            Error OperationStructureProblem.Command
        else
            match successor command.ExpectedRevision with
            | None -> Error OperationStructureProblem.Command
            | Some revision ->
                let payment =
                    match command.Act, command.SelectedPayment with
                    | CaseActKind.RecordPayment, _ ->
                        Some(PaymentReference.Recorded header.OperationId)
                    | CaseActKind.RemovePayment, Some selected ->
                        Some(PaymentReference.Removed selected)
                    | _ -> None

                let effect =
                    EffectDescriptor.Case(
                        command.CaseId,
                        revision,
                        command.Act,
                        (if opening then Some command.Reference else None),
                        payment
                    )

                if CaseCommandSkeleton.compartment command = None then
                    Ok([ effect ], [])
                else
                    descriptor (CompartmentOwner.Case command.CaseId) CompartmentPurpose.Case [ 0 ]
                    |> Result.map (fun content -> [ effect ], [ content ])

    let private authorityShape header (command: AuthorityCommandSkeleton) =
        let target = command.TargetActorId
        let bind = List.map (fun effect -> EffectDescriptor.Authority(target, effect))

        let profile index effects =
            descriptor (CompartmentOwner.ActorProfile target) CompartmentPurpose.Profile [ index ]
            |> Result.map (fun content -> bind effects, [ content ])

        let registering =
            match command.Act with
            | AuthorityAct.Register _
            | AuthorityAct.Admit _
            | AuthorityAct.ProvisionOwner _ -> true
            | _ -> false

        let duplicateGrants grants = List.distinct grants <> grants

        if registering && target <> ActorId.ofOperationId header.OperationId then
            Error OperationStructureProblem.Command
        else
            match command.Act with
            | AuthorityAct.Register 0 ->
                profile
                    1
                    [
                        AuthorityEffectDescriptor.Register command.Principal
                        AuthorityEffectDescriptor.SetProfile 0
                    ]
            | AuthorityAct.Admit value when
                value.Profile = 0
                && not value.Grants.IsEmpty
                && value.Grants.Length <= AuthorityAdmission.MaximumGrants
                && not (duplicateGrants value.Grants)
                ->
                profile
                    1
                    (AuthorityEffectDescriptor.Register command.Principal
                     :: AuthorityEffectDescriptor.SetProfile 0
                     :: (value.Grants |> List.map AuthorityEffectDescriptor.Grant))
            | AuthorityAct.ProvisionOwner(0, roles) when
                header.Sequence = Sequence.first
                && header.ActorId = target
                && PrincipalKey.isHuman command.Principal
                && Result.isOk (Authority.validateInitialOwnerRoles roles)
                ->
                profile
                    1
                    [
                        yield AuthorityEffectDescriptor.ProvisionOwner command.Principal
                        yield AuthorityEffectDescriptor.SetProfile 0
                        for role in roles ->
                            AuthorityEffectDescriptor.Grant
                                {
                                    Role = role
                                    Scope = Scope.Installation
                                }
                    ]
            | AuthorityAct.SetProfile 0 -> profile 0 [ AuthorityEffectDescriptor.SetProfile 0 ]
            | AuthorityAct.EraseProfile -> Ok(bind [ AuthorityEffectDescriptor.EraseProfile ], [])
            | AuthorityAct.Grant grant -> Ok(bind [ AuthorityEffectDescriptor.Grant grant ], [])
            | AuthorityAct.Revoke grant -> Ok(bind [ AuthorityEffectDescriptor.Revoke grant ], [])
            | AuthorityAct.Enable -> Ok(bind [ AuthorityEffectDescriptor.Enable ], [])
            | AuthorityAct.Disable -> Ok(bind [ AuthorityEffectDescriptor.Disable ], [])
            | _ -> Error OperationStructureProblem.Command

    let private reminderShape header (command: ReminderCommandSkeleton) =
        let next = ReminderId.ofOperationId header.OperationId

        let shape =
            match command.Act, command.ReminderId, command.ExpectedRevision with
            | ReminderActKind.Create, None, None ->
                Some [ EffectDescriptor.ReminderCreated(command.CaseId, next, Revision.first) ]
            | act, Some target, Some expected when
                act <> ReminderActKind.Create && expected >= Revision.first
                ->
                successor expected
                |> Option.bind (fun revision ->
                    if act = ReminderActKind.CompleteAndPlanNext then
                        if target = next then
                            None
                        else
                            Some
                                [
                                    EffectDescriptor.ReminderChanged(
                                        command.CaseId,
                                        target,
                                        revision,
                                        ReminderActKind.Complete
                                    )
                                    EffectDescriptor.ReminderCreated(
                                        command.CaseId,
                                        next,
                                        Revision.first
                                    )
                                ]
                    else
                        Some
                            [
                                EffectDescriptor.ReminderChanged(
                                    command.CaseId,
                                    target,
                                    revision,
                                    act
                                )
                            ])
            | _ -> None

        match shape with
        | None -> Error OperationStructureProblem.Command
        | Some effects ->
            descriptor
                (CompartmentOwner.Case command.CaseId)
                CompartmentPurpose.Reminder
                [ 0 .. effects.Length - 1 ]
            |> Result.map (fun content -> effects, [ content ])

    /// Derivation owns the expected closure; callers cannot invent independent child headers.
    let expected header command =
        match command with
        | CommandSkeleton.Case value -> caseShape header value
        | CommandSkeleton.Authority value -> authorityShape header value
        | CommandSkeleton.Reminder value -> reminderShape header value
        | CommandSkeleton.Work(parent, expected) ->
            match successor expected with
            | None -> Error OperationStructureProblem.Command
            | Some revision ->
                descriptor (CompartmentOwner.Case parent) CompartmentPurpose.Work [ 0 ]
                |> Result.map (fun content ->
                    [ EffectDescriptor.Work(parent, revision) ], [ content ])
        | CommandSkeleton.Note(NoteCommandSkeleton.Add parent) ->
            descriptor (CompartmentOwner.Case parent) CompartmentPurpose.Note [ 0 ]
            |> Result.map (fun content ->
                [ EffectDescriptor.NoteAdded(parent, NoteId.ofOperationId header.OperationId) ],
                [ content ])
        | CommandSkeleton.Note(NoteCommandSkeleton.Remove(parent, note)) ->
            Ok([ EffectDescriptor.NoteRemoved(parent, note) ], [])

    let validateHeader (header: OperationHeader) command =
        let authority =
            match command with
            | CommandSkeleton.Authority _ -> true
            | _ -> false

        if
            header.InstallationId = Guid.Empty
            || header.Sequence < Sequence.first
            || not (RecordedInstant.hasMicrosecondPrecision header.RecordedAt)
            || (authority = header.BusinessDate.IsSome)
        then
            Error OperationStructureProblem.Header
        else
            Ok()

    let private assemble header command effects compartments =
        match validateHeader header command with
        | Error error -> Error error
        | Ok() ->
            expected header command
            |> Result.bind (fun (requiredEffects, requiredContents) ->
                if
                    effects <> requiredEffects
                    || effects.IsEmpty
                    || effects.Length > MaximumEffects
                then
                    Error OperationStructureProblem.Effects
                elif
                    (compartments |> List.map (fun (content: Compartment) -> content.Descriptor))
                    <> requiredContents
                then
                    Error OperationStructureProblem.Compartments
                elif
                    compartments
                    |> List.exists (fun content ->
                        content.InstallationId <> header.InstallationId
                        || content.OperationId <> header.OperationId)
                then
                    Error OperationStructureProblem.CompartmentBinding
                else
                    Ok
                        {
                            HeaderValue =
                                { header with
                                    RecordedAt = header.RecordedAt.ToUniversalTime()
                                }
                            CommandValue = command
                            EffectsValue = effects
                            CompartmentsValue = compartments
                        })

    /// Stored erased compartments remain structurally interpretable; replay must prove their erasure.
    let restore header command effects compartments =
        assemble header command effects compartments

    /// A new append must carry every required private value, never a pre-erased placeholder.
    let append header command effects compartments =
        assemble header command effects compartments
        |> Result.bind (fun value ->
            if value.Compartments |> List.exists (Compartment.content >> Option.isNone) then
                Error OperationStructureProblem.UnavailableAppendContent
            else
                Ok value)
