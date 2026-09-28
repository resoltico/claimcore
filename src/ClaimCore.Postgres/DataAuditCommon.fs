namespace ClaimCore.Postgres

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json

module internal DataAuditCommon =
    let corrupt () =
        raise (InvalidDataException("Accepted case history failed the full data audit."))

    let witnessProof action =
        try
            action ()
        with
        | WitnessPending
        | :? InvalidOperationException
        | :? CryptographicException
        | :? JsonException
        | :? KeyNotFoundException
        | :? FormatException -> corrupt ()

    let businessZone zoneId =
        try
            TimeZoneInfo.FindSystemTimeZoneById(zoneId)
        with
        | :? TimeZoneNotFoundException
        | :? InvalidTimeZoneException -> corrupt ()
