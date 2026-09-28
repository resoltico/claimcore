namespace ClaimCore.Database

open System
open ClaimCore.Application
open ClaimCore.Postgres

[<RequireQualifiedAccess>]
type DatabaseOption =
    | SettledRetention
    | AbandonedRetention
    | Limit
    | DryRun

module DatabaseOptions =
    let all =
        [
            DatabaseOption.SettledRetention
            DatabaseOption.AbandonedRetention
            DatabaseOption.Limit
            DatabaseOption.DryRun
        ]

    let token =
        function
        | DatabaseOption.SettledRetention -> "--settled-retention-days"
        | DatabaseOption.AbandonedRetention -> "--abandoned-retention-days"
        | DatabaseOption.Limit -> "--limit"
        | DatabaseOption.DryRun -> "--dry-run"

    let maximum =
        function
        | DatabaseOption.SettledRetention
        | DatabaseOption.AbandonedRetention -> 3650
        | DatabaseOption.Limit -> 1000
        | DatabaseOption.DryRun -> 1

[<RequireQualifiedAccess>]
type DatabaseInputProblem =
    | UnsupportedInvocation
    | UnknownOption
    | MissingOptionValue of DatabaseOption
    | RepeatedOption of DatabaseOption
    | OptionOutOfRange of DatabaseOption
    | ConnectionSettingMissing
    | ConnectionFileRefused
    | ConnectionFileEmpty
    | WitnessSettingMissing
    | WitnessFileRefused
    | WitnessFileInvalid
    | PrincipalFileRefused
    | PrincipalFileInvalid
    | ManagedCopyFileRefused
    | ErasureProposalFileRefused
    | SuppressionKeyFileRefused
    | RestoreEvidenceFileRefused
    | BackupHealthFileRefused
    | WriterHandoffFileRefused
    | PhysicalCopyProofFileRefused
    | ProcessFailed
    | OutputDeliveryFailed

[<NoEquality; NoComparison>]
type FencedTailPaths =
    {
        Report: string
        ReportSignature: string
        EvidenceIndex: string
        Fence: string
        FenceSignature: string
        Supplement: string
        SupplementSignature: string
    }

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type DatabaseCommand =
    | Help
    | Version
    | VersionJson
    | Diagnostics
    | Initialize of string
    | InitializeRealData of string
    | PublishRealDataActivationPlan of
        policyFile: string *
        evidenceFile: string *
        outputFile: string
    | ActivateRealData of
        policyFile: string *
        originalEvidenceFile: string *
        freshEvidenceFile: string *
        planId: Guid *
        firstApprovalId: Guid *
        secondApprovalId: Guid
    | ReconcileRealDataActivation
    | InitializeWitness
    | ProvisionInitialOwner
    | Verify
    | VerifyData
    | HoldBackupCapture
    | ReconcileBackupCapture of leaseId: Guid
    | IssueBackupHealth of policyFile: string * evidenceFile: string * outputFile: string
    | ReconcileBackupHealth of policyFile: string * evidenceFile: string * outputFile: string
    | Prune of PreparationPruneOptions
    | RegisterCopySigner of CopySignerPurpose * Guid * Guid * string * Guid * Guid
    | RetireCopySigner of CopySignerPurpose * Guid * Guid * Guid * Guid
    | IngestManagedCopy of string * string
    | TransitionManagedCopy of string * string
    | VerifyDeleteManagedCopy of string * string
    | VerifyManagedCopy of string * string
    | AdoptManagedCopy of proposalFile: string
    | PublishExternalCopy of proposalFile: string
    | TransitionAdoptedCopy of canonicalFile: string * signatureFile: string
    | VerifyDeleteAdoptedCopy of canonicalFile: string * signatureFile: string
    | CertifyManagedPayloadAbsence of proposalFile: string
    | CompleteSuppressionHorizon of proposalFile: string
    | PrepareWriterHandoff of canonicalFile: string * signatureFile: string
    | SettleWriterHandoff of canonicalFile: string * signatureFile: string
    | ActivateWriterHandoff of FencedTailPaths
    | AbortWriterHandoff of candidate: string * signatureOne: string * signatureTwo: string
    | DraftWriterHandoffAbort of
        handoffId: Guid *
        keyOneId: Guid *
        keyTwoId: Guid *
        outputFile: string
    | PurgeLive of string
    | PruneWitnessPayload of string
    | InspectManagedCopy of Guid
    | ReconcileLifecycleEvent of Guid
    | VerifyRestoreReport of
        report: string *
        signature: string *
        evidenceIndex: string *
        nonce: string
    | VerifyFencedTail of FencedTailPaths * nonce: string
