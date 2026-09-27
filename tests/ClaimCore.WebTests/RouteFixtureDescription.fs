module internal ClaimCore.WebTests.RouteFixtureDescription

open System
open ClaimCore.Application

let description =
    {
        Contract = SemanticContract.current
        SemanticFingerprint = SemanticContract.fingerprint SemanticContract.current
        Runtime =
            {
                ProductVersion = "0.1.0"
                EffectiveBusinessDate = DateOnly(2026, 9, 9)
                TimeZoneId = "Etc/UTC"
            }
    }
