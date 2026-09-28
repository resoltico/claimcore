namespace ClaimCore.Contracts

open System.Globalization
open System.Text.Json
open ClaimCore.Application

module internal WebWireSignerApproval =
    let write (writer: Utf8JsonWriter) =
        function
        | CopySignerApprovalOutcome.Approved(approvalId, revision) ->
            WebWireQueries.outcome writer "APPROVED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("approvalId", approvalId)

                writer.WriteString(
                    "authorityRevision",
                    revision.ToString(CultureInfo.InvariantCulture)
                )

                writer.WriteEndObject())
        | CopySignerApprovalOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue
        | CopySignerApprovalOutcome.StartedUnconfirmed approvalId ->
            WebWireQueries.outcome writer "STARTED_UNCONFIRMED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("approvalId", approvalId)
                writer.WriteEndObject())
