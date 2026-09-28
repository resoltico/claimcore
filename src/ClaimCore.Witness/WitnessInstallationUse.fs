namespace ClaimCore.Witness

open System

[<RequireQualifiedAccess>]
type InstallationUseScope =
    | SyntheticOnly
    | RealData

[<RequireQualifiedAccess>]
type InstallationUsePhase =
    | BootstrapNoCases
    | Active

[<NoEquality; NoComparison>]
type InstallationUseState =
    {
        Scope: InstallationUseScope
        Phase: InstallationUsePhase
        ActivationEventId: Guid option
        ActivationSequence: int64 option
        ActivationHash: byte array option
    }

module InstallationUse =
    let scopeToken =
        function
        | InstallationUseScope.SyntheticOnly -> "SYNTHETIC_ONLY"
        | InstallationUseScope.RealData -> "REAL_DATA"

    let phaseToken =
        function
        | InstallationUsePhase.BootstrapNoCases -> "BOOTSTRAP_NO_CASES"
        | InstallationUsePhase.Active -> "ACTIVE"

    let parseScope =
        function
        | "SYNTHETIC_ONLY" -> InstallationUseScope.SyntheticOnly
        | "REAL_DATA" -> InstallationUseScope.RealData
        | _ -> invalidOp "Installation data-use scope is invalid."

    let parsePhase =
        function
        | "BOOTSTRAP_NO_CASES" -> InstallationUsePhase.BootstrapNoCases
        | "ACTIVE" -> InstallationUsePhase.Active
        | _ -> invalidOp "Installation data-use phase is invalid."
