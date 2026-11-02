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

type AccountSubject = { ActorId: ActorId; Subject: string }

type AssignedAccount =
    {
        Account: AccountSubject
        Available: bool
    }

type CaseWorkView =
    {
        Responsible: AssignedAccount option
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

type CaseAdvice =
    private
        {
            MaintenanceState: RegisterMaintenance
            OfferedActs: CaseActKind list
            CorrectionChoices: CorrectionChoices option
            HistoryDetails: HistoryDetail list
        }

    member this.Maintenance = this.MaintenanceState
    member this.Acts = this.OfferedActs
    member this.Correction = this.CorrectionChoices
    member this.History = this.HistoryDetails

module CaseAdvice =
    let create acts correction history =
        if List.contains CaseActKind.Correct acts <> Option.isSome correction then
            Error "Correction choices must be present exactly when Correct is offered."
        else
            Ok
                {
                    MaintenanceState = RegisterMaintenance.initial
                    OfferedActs = acts
                    CorrectionChoices = correction
                    HistoryDetails = history
                }

    let duringMaintenance maintenance (advice: CaseAdvice) =
        { advice with
            MaintenanceState = maintenance
            OfferedActs = if maintenance.Active then [] else advice.Acts
            CorrectionChoices = if maintenance.Active then None else advice.Correction
        }

    let createDuringMaintenance
        (maintenance: RegisterMaintenance)
        acts
        (correction: CorrectionChoices option)
        history
        =
        if maintenance.Active && (not (List.isEmpty acts) || correction.IsSome) then
            Error "Maintenance advice cannot offer register writes."
        else
            create acts correction history |> Result.map (duringMaintenance maintenance)

type ActorContext =
    {
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
    }

type FieldDiff =
    {
        FieldName: BusinessField
        Before: string option
        After: string option
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
