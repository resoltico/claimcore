namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// Refusals carry neither the private path nor its content.
[<RequireQualifiedAccess>]
type PrivateFileFailure =
    | NotAbsolute
    | InvalidPath
    | InvalidLimit
    | Missing
    | NotRegular
    | TooPermissive
    | TooLarge
    | Unreadable
    | UnsupportedPlatform

/// Bounded private inputs admitted from one nonblocking Unix descriptor.
module PrivateFile =
    let private forbidden =
        UnixFileMode.OtherRead
        ||| UnixFileMode.OtherWrite
        ||| UnixFileMode.OtherExecute
        ||| UnixFileMode.GroupWrite
        ||| UnixFileMode.GroupExecute

    let private admitted mode = mode &&& forbidden = UnixFileMode.None

    let internal readBytes read (buffer: byte array) =
        try
            read buffer
            Ok buffer
        with _ ->
            CryptographicOperations.ZeroMemory(Span<byte> buffer)
            reraise ()

    let internal readHandle maximumBytes abi (handle: UnixPrivateFile.Handle) =
        use owned = handle
        let mode, length = UnixPrivateFile.metadata abi handle

        if mode &&& 0xF000 <> 0x8000 then
            Error PrivateFileFailure.NotRegular
        elif not (admitted (enum<UnixFileMode>(mode &&& 0xFFF))) then
            Error PrivateFileFailure.TooPermissive
        elif length < 0L || length > int64 maximumBytes then
            Error PrivateFileFailure.TooLarge
        else
            readBytes (UnixPrivateFile.read handle) (Array.zeroCreate<byte>(int length))

    let private validPath (path: string) =
        if path.Contains '\000' then
            false
        else
            try
                UTF8Encoding(false, true).GetByteCount path |> ignore
                true
            with :? EncoderFallbackException ->
                false

    let private openRead maximumBytes path =
        match UnixPrivateFile.platform () with
        | None -> Error PrivateFileFailure.UnsupportedPlatform
        | Some abi ->
            let pathBytes = Encoding.UTF8.GetBytes(path + "\000")
            let handle = UnixPrivateFile.openRead abi pathBytes
            readHandle maximumBytes abi handle

    /// The caller owns the returned bytes and must clear them after use. Links are followed.
    let internal readOnPlatform supported maximumBytes (path: string) =
        if not supported then
            Error PrivateFileFailure.UnsupportedPlatform
        elif maximumBytes < 0 then
            Error PrivateFileFailure.InvalidLimit
        elif String.IsNullOrEmpty path || not (Path.IsPathFullyQualified path) then
            Error PrivateFileFailure.NotAbsolute
        elif not (validPath path) then
            Error PrivateFileFailure.InvalidPath
        else
            try
                openRead maximumBytes path
            with
            | :? FileNotFoundException
            | :? DirectoryNotFoundException -> Error PrivateFileFailure.Missing
            | :? IOException
            | :? UnauthorizedAccessException -> Error PrivateFileFailure.Unreadable

    let read maximumBytes path =
        readOnPlatform (UnixPrivateFile.platform () |> Option.isSome) maximumBytes path
