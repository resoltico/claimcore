namespace ClaimCore.Postgres

open System
open System.Data.Common
open System.Text.Json
open ClaimCore.Witness

/// Read-only, bounded comparison of one committed primary row with its encrypted candidate.
module internal WitnessPrimaryMatch =
    let private optionalActor (root: JsonElement) (name: string) (reader: DbDataReader) ordinal =
        let value = root.GetProperty(name)

        if reader.IsDBNull(ordinal) then
            value.ValueKind = JsonValueKind.Null
        else
            value.ValueKind = JsonValueKind.String
            && value.GetGuid() = reader.GetGuid(ordinal)

    let private attribution (root: JsonElement) (reader: DbDataReader) =
        root.GetProperty("preparerActorId").GetGuid() = reader.GetGuid(11)
        && optionalActor root "importerActorId" reader 12
        && optionalActor root "submitterActorId" reader 13
        && optionalActor root "resolverActorId" reader 14
        && root.GetProperty("acceptedActorId").GetGuid() = reader.GetGuid(15)
        && root.GetProperty("grantRevision").GetInt64() = reader.GetInt64(16)

    let private identity (root: JsonElement) (reader: DbDataReader) operationId =
        root.GetProperty("version").GetInt32() = 2
        && root.GetProperty("ruleRevision").GetInt32() = 1
        && reader.GetInt16(6) = 1s
        && root.GetProperty("operationId").GetGuid() = operationId
        && root.GetProperty("caseId").GetGuid() = reader.GetGuid(10)
        && root.GetProperty("caseReference").GetString() = reader.GetString(0)
        && root.GetProperty("newRevision").GetInt64() = reader.GetInt64(1)
        && root.GetProperty("expectedRevision").GetInt64() = reader.GetInt64(1) - 1L

    let private payload (root: JsonElement) (reader: DbDataReader) =
        let captured = root.GetProperty("observedUtcInstant").GetDateTimeOffset()
        let observed = reader.GetFieldValue<DateTimeOffset>(5)

        root.GetProperty("canonicalRequest").GetBytesFromBase64() =
            reader.GetFieldValue<byte array>(2)
        && root.GetProperty("canonicalSnapshot").GetBytesFromBase64() =
            reader.GetFieldValue<byte array>(3)
        && root.GetProperty("effectiveBusinessDate").GetString() = reader.GetString(4)
        && abs (captured.Subtract(observed).Ticks) <= 10L

    let private ticket (ticket: Ticket) (reader: DbDataReader) =
        reader.GetInt64(7) = ticket.Sequence
        && reader.GetInt64(8) = ticket.Epoch
        && reader.GetFieldValue<byte array>(9) = ticket.EntryHash

    let accepted operationId intentTicket root (reader: DbDataReader) =
        identity root reader operationId
        && attribution root reader
        && payload root reader
        && ticket intentTicket reader
