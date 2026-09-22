namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Security.Cryptography
open System.Text

module PrivateFileService =
    let private utf8 = UTF8Encoding(false, true)
    let private maximumPrivateBytes = 8 * 1024 * 1024

    let private supported () =
        OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()

    let private invalid () = Error PrivateFileFailure.AccessRefused

    let private safeCall action =
        try
            PosixPrivateNative.verifyShim ()
            Ok(action ())
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException
        | :? DllNotFoundException
        | :? EntryPointNotFoundException -> invalid ()

    let private validMaximum maximum =
        maximum >= 1 && maximum <= maximumPrivateBytes

    let readBinary maximum path =
        if not (supported ()) then
            Error PrivateFileFailure.UnsupportedPlatform
        elif not (validMaximum maximum) then
            Error PrivateFileFailure.InvalidLimit
        else
            match safeCall (fun () -> PosixPrivateFiles.read maximum path) with
            | Error error -> Error error
            | Ok outcome -> outcome

    let readUtf8Bytes maximum path =
        match readBinary maximum path with
        | Error error -> Error error
        | Ok bytes ->
            try
                utf8.GetCharCount(bytes) |> ignore
                Ok bytes
            with :? DecoderFallbackException ->
                CryptographicOperations.ZeroMemory(Span<byte>(bytes))
                Error PrivateFileFailure.InvalidUtf8

    let readUtf8Text maximum path =
        match readUtf8Bytes maximum path with
        | Error error -> Error error
        | Ok bytes ->
            try
                Ok(utf8.GetString(bytes))
            finally
                CryptographicOperations.ZeroMemory(Span<byte>(bytes))

    let writeNew maximum path (bytes: byte array) =
        if not (supported ()) then
            Error PrivateFileFailure.UnsupportedPlatform
        elif not (validMaximum maximum) then
            Error PrivateFileFailure.InvalidLimit
        elif not (obj.ReferenceEquals(bytes, null)) && bytes.Length > maximum then
            Error PrivateFileFailure.TooLarge
        else
            match safeCall (fun () -> PosixPrivateFiles.writeNew path bytes) with
            | Error error -> Error error
            | Ok outcome -> outcome

    let ensureDirectory path =
        if not (supported ()) then
            Error PrivateFileFailure.UnsupportedPlatform
        else
            safeCall (fun () -> PosixPrivateFiles.ensureDirectory path)

    let openExclusive path =
        if not (supported ()) then
            Error PrivateFileFailure.UnsupportedPlatform
        else
            safeCall (fun () -> PosixPrivateFiles.openExclusive path)

    let deletePrivate path =
        if not (supported ()) then
            Error PrivateFileFailure.UnsupportedPlatform
        else
            safeCall (fun () -> PosixPrivateFiles.deletePrivate path)
