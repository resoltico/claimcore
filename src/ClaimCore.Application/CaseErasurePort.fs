namespace ClaimCore.Application

open System

/// Installation-bound, owner-private keyed commitments. The key and its check value stay
/// outside PostgreSQL and never cross a transport boundary. Inputs are exact accepted bytes.
type ISuppressionCommitments =
    abstract InstallationId: Guid
    abstract LineageId: Guid
    abstract KeyId: Guid
    abstract Admit: unit -> unit
    abstract Reference: caseReference: string -> byte array
    abstract Operation: operationId: Guid -> byte array
    abstract RequestCandidate: canonical: byte array -> byte array
    abstract PurgeProposal: canonicalDraft: byte array -> byte array
    abstract ApprovalDraft: canonicalDraft: byte array -> byte array
    abstract ApprovalCanonical: canonicalApproval: byte array -> byte array
