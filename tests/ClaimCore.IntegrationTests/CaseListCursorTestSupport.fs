module internal ClaimCore.IntegrationTests.CaseListCursorTestSupport

open System
open ClaimCore.Application
open ClaimCore.Hosting

/// Synthetic test-only runtime key shared by independently opened store adapters in one fixture.
let protection =
    new CaseListCursorProtection(Array.init 32 (fun index -> byte (index + 1)))
    :> ICaseListCursorProtection

let clock =
    { new IBusinessTime with
        member _.Capture() =
            {
                ObservedUtcInstant = DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)
                EffectiveBusinessDate = DateOnly(2026, 9, 7)
                TimeZoneId = "Etc/UTC"
            }
    }
