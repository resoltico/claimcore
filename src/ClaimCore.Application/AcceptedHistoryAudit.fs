namespace ClaimCore.Application

open ClaimCore.Domain

/// Historical replay remains an Application decision. Storage supplies immutable evidence but
/// cannot reinterpret commands or decide whether a resulting business state is valid.
module internal AcceptedHistoryAudit =
    let replay today request previous recorded =
        match Claim.decide today request previous with
        | Ok value when Claim.view value = Claim.view recorded -> Some value
        | _ -> None
