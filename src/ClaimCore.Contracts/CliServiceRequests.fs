namespace ClaimCore.Contracts

open System
open System.Buffers
open System.Text.Json

/// CLI-only local destination paths never cross the service boundary. Contracts owns the exact
/// JSON body sent to the generated Web-v3 recovery-export endpoint.
module CliServiceRequests =
    let export (operationId: Guid) (requestSha256: string) =
        let buffer = ArrayBufferWriter<byte>()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()
        writer.WriteString("operationId", operationId)
        writer.WriteString("requestSha256", requestSha256)
        writer.WriteEndObject()
        writer.Flush()
        buffer.WrittenSpan.ToArray()
