namespace ClaimCore.Domain

open System

/// Closed nonclaimant codes for authority changes after the live case rows are removed.
module TombstoneHoldPolicy =
    let groundCodes =
        [ "LEGAL_RETENTION"; "REGULATORY_HOLD"; "DISPUTE"; "SECURITY_INCIDENT" ]

    let releaseCodes = [ "LEGAL_RELEASE"; "REVIEW_CLOSED"; "EXPIRED_WITH_REVIEW" ]

    let validGround value = groundCodes |> List.contains value
    let validRelease value = releaseCodes |> List.contains value

    let validateRecord (holdId: Guid) ground reviewOn (observedAt: DateTimeOffset) =
        if holdId = Guid.Empty then
            Error LifecycleRefusal.InvalidIdentity
        elif not (validGround ground) then
            Error LifecycleRefusal.InvalidReason
        elif observedAt.Offset <> TimeSpan.Zero then
            Error LifecycleRefusal.InvalidTime
        else
            let today = DateOnly.FromDateTime(observedAt.UtcDateTime)

            if reviewOn < today || reviewOn > today.AddYears(10) then
                Error LifecycleRefusal.InvalidTime
            else
                Ok()

    let validateRelease (holdId: Guid) releaseCode =
        if holdId = Guid.Empty then
            Error LifecycleRefusal.InvalidIdentity
        elif not (validRelease releaseCode) then
            Error LifecycleRefusal.InvalidReason
        else
            Ok()
