namespace ClaimCore.Postgres

open System
open System.Threading.Tasks
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

    let witnessProofAsync (action: unit -> Task<'value>) =
        task {
            try
                return! action ()
            with
            | WitnessPending
            | :? InvalidOperationException
            | :? CryptographicException
            | :? JsonException
            | :? KeyNotFoundException
            | :? FormatException -> return corrupt ()
        }

    let businessZone zoneId =
        try
            TimeZoneInfo.FindSystemTimeZoneById(zoneId)
        with
        | :? TimeZoneNotFoundException
        | :? InvalidTimeZoneException -> corrupt ()
