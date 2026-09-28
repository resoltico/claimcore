namespace ClaimCore.Contracts

open System.Globalization
open System.Text.Json
open ClaimCore.Application

module internal WebWireManagement =
    let write (writer: Utf8JsonWriter) (result: ActorManagementOutcome) =
        match result with
        | ActorManagementOutcome.Applied(eventId, revision, actorId) ->
            WebWireQueries.outcome writer "APPLIED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("eventId", eventId)

                writer.WriteString(
                    "grantRevision",
                    revision.ToString(CultureInfo.InvariantCulture)
                )

                writer.WriteString("targetActorId", actorId)
                writer.WriteEndObject())
        | ActorManagementOutcome.ResourceUnavailable ->
            WebWireQueries.outcome writer "RESOURCE_UNAVAILABLE" writer.WriteNullValue
        | ActorManagementOutcome.Unconfirmed eventId ->
            WebWireQueries.outcome writer "UNCONFIRMED" (fun () ->
                writer.WriteStartObject()
                writer.WriteString("eventId", eventId)
                writer.WriteEndObject())
