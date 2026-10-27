namespace ClaimCore.Application

open System
open ClaimCore.Domain

module VerificationChecks =
    type Builder() =
        member _.Bind(value, binder) = Result.bind binder value
        member _.Return(value) = Ok value
        member _.ReturnFrom(value) = value
        member _.Zero() = Ok()

    let result = Builder()

    let divergence sequence finding =
        {
            Sequence = sequence
            Finding = finding
        }

    let require sequence finding condition =
        if condition then
            Ok()
        else
            Error(divergence sequence finding)

    let capture sequence finding read =
        try
            Ok(read ())
        with
        | :? InvalidOperationException
        | :? InvalidCastException
        | :? ArgumentException
        | :? FormatException
        | :? OverflowException -> Error(divergence sequence finding)

    let known sequence finding =
        function
        | Some value -> Ok value
        | None -> Error(divergence sequence finding)
