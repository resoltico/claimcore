namespace ClaimCore.Application.Contracts

open ClaimCore.Domain


open System

type RuntimeContext =
    {
        ProductVersion: string
        EffectiveBusinessDate: DateOnly
        TimeZoneId: string
    }

/// The database snapshot and installation zone define due state, never the display clock.
type WorkDateBasis =
    {
        ObservedAt: DateTimeOffset
        BusinessDate: DateOnly
        TimeZoneId: string
    }

type CaseWorkView =
    {
        Revision: Revision
        ChangedAt: DateTimeOffset option
        RecordedBy: ActorRef option
        Summary: HandlingSummary option
        NextAction: ReminderAction option
        PendingReminders: int64
        OverdueReminders: int64
        NeedsNextAction: bool
        CanUpdateSummary: bool
        CanAssign: bool
        CanCreateReminder: bool
        Responsible: ActorRef option
        FollowUp: CalendarDate option
        Due: CaseDue
        Basis: WorkDateBasis
    }

[<RequireQualifiedAccess>]
type View =
    | Cases
    | Authority
    | Operations

[<RequireQualifiedAccess>]
type InstallationOffer =
    | Open
    | AuthorityAct of AuthorityKind

type CorrectionChoices =
    {
        Registration: CorrectionGroupAction list
        Decision: CorrectionGroupAction list
        Payment: CorrectionGroupAction list
    }

type CaseActRefusal = { Act: CaseActKind; Reason: Refusal }

type CaseAdvice =
    private
        {
            MaintenanceState: RegisterMaintenance
            OfferedActs: CaseActKind list
            CorrectionChoices: CorrectionChoices option
            HistoryDetails: HistoryDetail list
            RecommendedAct: CaseActKind option
            BlockedActs: CaseActRefusal list
        }

    member this.Maintenance = this.MaintenanceState
    member this.Acts = this.OfferedActs
    member this.Correction = this.CorrectionChoices
    member this.History = this.HistoryDetails
    member this.Recommended = this.RecommendedAct
    member this.Blocked = this.BlockedActs

module CaseAdvice =
    let private availabilityReason =
        function
        | Refusal.Case(CaseRefusal.CaseAlreadyExists | CaseRefusal.NotOffered _)
        | Refusal.Access _
        | Refusal.RegisterMaintenance -> true
        | _ -> false

    let create acts correction history blocked =
        if List.contains CaseActKind.Correct acts <> Option.isSome correction then
            Error "Correction choices must be present exactly when Correct is offered."
        elif blocked |> List.exists (fun blocked -> not (availabilityReason blocked.Reason)) then
            Error "An unavailable act requires a permission, maintenance or state reason."
        elif
            (blocked |> List.map _.Act |> Set.ofList)
            <> Set.difference (Set.ofList CaseActKind.all) (Set.ofList acts)
            || (blocked |> List.map _.Act |> List.distinct |> List.length)
               <> List.length blocked
        then
            Error
                "Every unavailable act must have exactly one reason, and offered acts must have none."
        else
            Ok
                {
                    MaintenanceState = RegisterMaintenance.initial
                    OfferedActs = acts
                    CorrectionChoices = correction
                    HistoryDetails = history
                    RecommendedAct = None
                    BlockedActs = blocked
                }

    let withRecommendation recommended (advice: CaseAdvice) =
        if recommended |> Option.exists (fun act -> not (List.contains act advice.Acts)) then
            Error "The recommended act must be offered."
        else
            Ok
                { advice with
                    RecommendedAct = recommended
                }

    let duringMaintenance maintenance (advice: CaseAdvice) =
        { advice with
            MaintenanceState = maintenance
            OfferedActs = if maintenance.Active then [] else advice.Acts
            CorrectionChoices = if maintenance.Active then None else advice.Correction
            RecommendedAct = if maintenance.Active then None else advice.Recommended
            BlockedActs =
                if maintenance.Active then
                    (advice.Blocked
                     |> List.map (fun blocked ->
                         { blocked with
                             Reason =
                                 match blocked.Reason with
                                 | Refusal.Access _ as refusal -> refusal
                                 | _ -> Refusal.RegisterMaintenance
                         }))
                    @ (advice.Acts
                       |> List.distinct
                       |> List.map (fun act ->
                           {
                               Act = act
                               Reason = Refusal.RegisterMaintenance
                           }))
                else
                    advice.Blocked
        }

    let createDuringMaintenance
        (maintenance: RegisterMaintenance)
        acts
        (correction: CorrectionChoices option)
        history
        blocked
        =
        if maintenance.Active && (not (List.isEmpty acts) || correction.IsSome) then
            Error "Maintenance advice cannot offer register writes."
        elif
            maintenance.Active
            && (blocked
                |> List.exists (fun blocked ->
                    match blocked.Reason with
                    | Refusal.Access _
                    | Refusal.RegisterMaintenance -> false
                    | _ -> true))
        then
            Error "Maintenance advice requires maintenance or permission reasons."
        else
            create acts correction history blocked
            |> Result.map (duringMaintenance maintenance)

type ActorContext =
    {
        Self: ActorRef
        Maintenance: RegisterMaintenance
        InstallationId: Guid
        ActorId: ActorId
        Runtime: RuntimeContext
        Capability: Capability list
        View: View list
        Offers: InstallationOffer list
        HasScopedCases: bool
    }

type CaseReadModel =
    {
        Snapshot: CaseSnapshot
        Advice: CaseAdvice
        Mine: bool
        Work: CaseWorkView
        Headline: CaseHeadline
        Attention: CaseAttention list
        Stages: CaseStageDisplay
    }

type AdvisoryReview =
    {
        Before: CaseSnapshot option
        Proposed: CaseSnapshot
        Changes: FieldDiff list
        Context: RuntimeContext
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module View =
    let vocabulary = Vocabulary.ofUnion<View> "view" TokenCasing.ScreamingSnake None
