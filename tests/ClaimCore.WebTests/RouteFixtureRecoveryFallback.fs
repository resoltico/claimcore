module ClaimCore.WebTests.RouteFixtureRecoveryFallback

open System
open ClaimCore.Application

let exportFallback operationId invalidMetadata =
    if invalidMetadata then
        RecoveryQueryOutcome.RecoverySucceeded(
            Lookup.Found
                {
                    Bytes = [||]
                    FileName = "synthetic-invalid-name.json"
                    MediaType = "application/vnd.claimcore.recovery+json"
                    RequestSha256 = String.replicate 64 "a"
                }
        )
    else
        RecoveryQueryOutcome.RecoverySucceeded(Lookup.NotFound operationId)
