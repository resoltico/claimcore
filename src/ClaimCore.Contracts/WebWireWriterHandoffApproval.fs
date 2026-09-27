namespace ClaimCore.Contracts

open System.Globalization
open System.Text.Json
open ClaimCore.Application

module internal WebWireWriterHandoffApproval =
    let write (writer: Utf8JsonWriter) =
        function
        | WriterHandoffApprovalOutcome.Approved(approvalId, revision) ->
            WebWireQueries.outcome writer "APPROVED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("approvalId", approvalId)

                writer.WriteString(
                    "authorityRevision",
                    revision.ToString(CultureInfo.InvariantCulture)
                )

                writer.WriteEndObject())
        | WriterHandoffApprovalOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue
        | WriterHandoffApprovalOutcome.StartedUnconfirmed approvalId ->
            WebWireQueries.outcome writer "STARTED_UNCONFIRMED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("approvalId", approvalId)
                writer.WriteEndObject())
