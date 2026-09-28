namespace ClaimCore.Witness

open System

type Identity =
    {
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
    }
