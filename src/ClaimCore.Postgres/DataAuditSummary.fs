namespace ClaimCore.Postgres

[<NoEquality; NoComparison>]
type internal PrimaryAuditCounts =
    {
        Cases: int64
        AcceptedOperations: int64
        LifecycleEvents: int64
        VerifiedCaseTipsSha256: byte array
        ErasureFences: int64
        TerminalApprovals: int64
        TerminalEvents: int64
        Revocations: int64
    }

[<NoEquality; NoComparison>]
type internal AuthorityAuditCounts =
    {
        AuthorityEvents: int64
        Actors: int64
        Grants: int64
        SignerApprovals: int64
        SignerKeys: int64
        SignerEvents: int64
        OwnerManagedCopies: int64
        CopyPhysicalVerifications: int64
        CopyDeletionApprovals: int64
        WriterHandoffApprovals: int64
        WriterHandoffPreparations: int64
        WriterHandoffs: int64
        WriterActivations: int64
        WriterHandoffAborts: int64
        ManagedExports: int64
        WitnessEntries: int64
        PendingIntents: int64
    }

[<NoEquality; NoComparison>]
type internal DataAuditSummary =
    {
        Cases: int64
        AcceptedOperations: int64
        LifecycleEvents: int64
        VerifiedCaseTipsSha256: byte array
        ErasureFences: int64
        TerminalApprovals: int64
        TerminalEvents: int64
        Revocations: int64
        AuthorityEvents: int64
        Actors: int64
        Grants: int64
        SignerApprovals: int64
        SignerKeys: int64
        SignerEvents: int64
        OwnerManagedCopies: int64
        CopyPhysicalVerifications: int64
        CopyDeletionApprovals: int64
        WriterHandoffApprovals: int64
        WriterHandoffPreparations: int64
        WriterHandoffs: int64
        WriterActivations: int64
        WriterHandoffAborts: int64
        ManagedExports: int64
        WitnessEntries: int64
        WitnessCutoff: int64
        PendingIntents: int64
    }

module internal DataAuditSummary =
    let create
        (primary: PrimaryAuditCounts)
        (authority: AuthorityAuditCounts)
        cutoff
        : DataAuditSummary =
        {
            Cases = primary.Cases
            AcceptedOperations = primary.AcceptedOperations
            LifecycleEvents = primary.LifecycleEvents
            VerifiedCaseTipsSha256 = primary.VerifiedCaseTipsSha256
            ErasureFences = primary.ErasureFences
            TerminalApprovals = primary.TerminalApprovals
            TerminalEvents = primary.TerminalEvents
            Revocations = primary.Revocations
            AuthorityEvents = authority.AuthorityEvents
            Actors = authority.Actors
            Grants = authority.Grants
            SignerApprovals = authority.SignerApprovals
            SignerKeys = authority.SignerKeys
            SignerEvents = authority.SignerEvents
            OwnerManagedCopies = authority.OwnerManagedCopies
            CopyPhysicalVerifications = authority.CopyPhysicalVerifications
            CopyDeletionApprovals = authority.CopyDeletionApprovals
            WriterHandoffApprovals = authority.WriterHandoffApprovals
            WriterHandoffPreparations = authority.WriterHandoffPreparations
            WriterHandoffs = authority.WriterHandoffs
            WriterActivations = authority.WriterActivations
            WriterHandoffAborts = authority.WriterHandoffAborts
            ManagedExports = authority.ManagedExports
            WitnessEntries = authority.WitnessEntries
            WitnessCutoff = cutoff
            PendingIntents = authority.PendingIntents
        }
