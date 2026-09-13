namespace ClaimCore.Application

open System
open System.Security.Cryptography
open ClaimCore.Domain
open ClaimCore.RecordFormat

/// Only the application can prepare the request admitted to a persistence transaction.
type internal PreparedOperation =
    private
        {
            RequestValue: CommandRequest
            FingerprintValue: string
        }

module internal Operation =
    let prepare request =
        Claim.validateRequest request
        |> Result.map (fun () ->
            {
                RequestValue = request
                FingerprintValue =
                    request |> RequestRecord.encode |> SHA256.HashData |> Convert.ToHexStringLower
            })

    let request operation = operation.RequestValue
    let fingerprint operation = operation.FingerprintValue
