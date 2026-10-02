module ClaimCore.IntegrationTests.RestoreReportScopeTests

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Database
open ClaimCore.IntegrationTests.RestoreProduceCanonicalTests

let private forgedReadiness (bytes: byte array) =
    use document = JsonDocument.Parse(bytes)
    let fields = SortedDictionary<string, JsonElement>(StringComparer.Ordinal)

    for property in document.RootElement.EnumerateObject() do
        fields[property.Name] <- property.Value.Clone()

    fields["realDataReady"] <- JsonSerializer.SerializeToElement(true)
    Encoding.ASCII.GetBytes(JsonSerializer.Serialize(fields) + "\n")

let tests =
    testCase
        "[CC-BACKUP-001] intermediate restore reports cannot claim deployment readiness"
        (fun _ ->
            let claims, _, _ = specimen ()

            for scope in [ "synthetic-only"; "full" ] do
                let bytes = DatabaseRestoreProduceCanonical.report { claims with Scope = scope }
                use document = JsonDocument.Parse(bytes)

                Expect.isFalse
                    (document.RootElement.GetProperty("realDataReady").GetBoolean())
                    "Both intermediate report scopes preserve their explicit non-readiness"

                Expect.isSome
                    (DatabaseRestoreReportClaims.parse bytes claims.CheckedAt)
                    "A correctly scoped intermediate report remains parseable"

                Expect.isNone
                    (DatabaseRestoreReportClaims.parse (forgedReadiness bytes) claims.CheckedAt)
                    "Changing the readiness claim cannot confer deployment authority")
