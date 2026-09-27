namespace ClaimCore.Contracts

open System.Globalization
open System.Text.Json
open ClaimCore.Application

module internal WebWireCopyAdoptionApproval =
    let write (writer: Utf8JsonWriter) =
        function
        | CopyAdoptionApprovalOutcome.Approved(approvalId, revision) ->
            WebWireQueries.outcome writer "APPROVED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("approvalId", approvalId)

                writer.WriteString(
                    "authorityRevision",
                    revision.ToString(CultureInfo.InvariantCulture)
                )

                writer.WriteEndObject())
        | CopyAdoptionApprovalOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue
        | CopyAdoptionApprovalOutcome.StartedUnconfirmed approvalId ->
            WebWireQueries.outcome writer "STARTED_UNCONFIRMED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("approvalId", approvalId)
                writer.WriteEndObject())
