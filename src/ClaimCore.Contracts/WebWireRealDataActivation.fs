namespace ClaimCore.Contracts

open System
open System.Globalization
open System.Text
open System.Text.Json
open ClaimCore.Application

module internal WebWireRealDataActivation =
    let private revision (writer: Utf8JsonWriter) (name: string) (value: int64) =
        writer.WriteString(name, value.ToString(CultureInfo.InvariantCulture))

    let private digest (writer: Utf8JsonWriter) (name: string) (value: byte array) =
        writer.WriteString(name, Convert.ToHexStringLower value)

    let private identity (writer: Utf8JsonWriter) (value: RealDataActivationPlanReview) =
        writer.WriteString("planId", value.PlanId)
        writer.WriteString("activationId", value.ActivationId)
        writer.WriteString("installationId", value.InstallationId)
        writer.WriteString("lineageId", value.LineageId)
        revision writer "epoch" value.Epoch
        revision writer "writerGeneration" value.WriterGeneration
        digest writer "policySha256" value.PolicySha256
        digest writer "publicationRootSha256" value.PublicationRootSha256

    let private physical (writer: Utf8JsonWriter) (value: RealDataActivationPlanReview) =
        writer.WriteString("cycleId", value.CycleId)
        writer.WriteString("leaseId", value.LeaseId)
        digest writer "captureReceiptSha256" value.CaptureReceiptSha256
        writer.WriteString("primaryBaseCopyId", value.PrimaryBaseCopyId)
        writer.WriteString("witnessBaseCopyId", value.WitnessBaseCopyId)
        digest writer "primaryBasePhysicalReceiptSha256" value.PrimaryBasePhysicalReceiptSha256
        digest writer "witnessBasePhysicalReceiptSha256" value.WitnessBasePhysicalReceiptSha256
        digest writer "checkpointObjectSha256" value.CheckpointObjectSha256
        digest writer "testRestoreReportSha256" value.TestRestoreReportSha256
        digest writer "testRestoreFullAuditSha256" value.TestRestoreFullAuditSha256

    let private publication (writer: Utf8JsonWriter) (value: RealDataActivationPlanReview) =
        revision writer "minimumArtifactCutoffSequence" value.MinimumArtifactCutoffSequence
        writer.WriteString("minimumPrimaryWalHorizon", value.MinimumPrimaryWalHorizon)
        writer.WriteString("minimumWitnessWalHorizon", value.MinimumWitnessWalHorizon)
        writer.WriteString("canonicalPlan", Encoding.ASCII.GetString value.CanonicalPlan)
        digest writer "planSha256" value.PlanSha256
        writer.WriteString("publishedAt", value.PublishedAt.ToUniversalTime().ToString("O"))
        revision writer "publicationWitnessSequence" value.PublicationWitnessSequence
        digest writer "publicationWitnessHash" value.PublicationWitnessHash

        writer.WriteString(
            "approvalExpiresNoLaterThan",
            value.ApprovalExpiresNoLaterThan.ToUniversalTime().ToString("O")
        )

    let review (writer: Utf8JsonWriter) =
        function
        | RealDataActivationPlanReviewOutcome.Reviewed value ->
            WebWireQueries.outcome writer "REVIEWED" (fun () ->
                writer.WriteStartObject()
                identity writer value
                physical writer value
                publication writer value
                writer.WriteEndObject())
        | RealDataActivationPlanReviewOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue

    let approval (writer: Utf8JsonWriter) =
        function
        | RealDataActivationApprovalOutcome.Approved(approvalId, authorityRevision) ->
            WebWireQueries.outcome writer "APPROVED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("approvalId", approvalId)
                revision writer "authorityRevision" authorityRevision
                writer.WriteEndObject())
        | RealDataActivationApprovalOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue
        | RealDataActivationApprovalOutcome.StartedUnconfirmed approvalId ->
            WebWireQueries.outcome writer "STARTED_UNCONFIRMED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("approvalId", approvalId)
                writer.WriteEndObject())
