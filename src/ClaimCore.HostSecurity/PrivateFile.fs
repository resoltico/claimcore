namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Microsoft.Win32.SafeHandles

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

/// Managed Unix admission, with permission and bounded positional reads from the opened handle.
module PrivateFile =
    let private forbidden =
        UnixFileMode.OtherRead
        ||| UnixFileMode.OtherWrite
        ||| UnixFileMode.OtherExecute
        ||| UnixFileMode.GroupWrite
        ||| UnixFileMode.GroupExecute

    let internal readBytes read (buffer: byte array) =
        try
            read buffer
            Ok buffer
        with _ ->
            CryptographicOperations.ZeroMemory(Span<byte> buffer)
            reraise ()

    let private readExactly handle (buffer: byte array) =
        let mutable offset = 0

        while offset < buffer.Length do
            let count =
                RandomAccess.Read(
                    handle,
                    Span<byte>(buffer, offset, buffer.Length - offset),
                    int64 offset
                )

            if count = 0 then
                raise (EndOfStreamException())

            offset <- offset + count

    let internal readHandle maximumBytes expectedLength (handle: SafeFileHandle) =
        use owned = handle

        if File.GetUnixFileMode handle &&& forbidden <> UnixFileMode.None then
            Error PrivateFileFailure.TooPermissive
        else
            let length = RandomAccess.GetLength handle

            if length <= 0L then
                Error PrivateFileFailure.NotRegular
            elif length > int64 maximumBytes then
                Error PrivateFileFailure.TooLarge
            elif length <> expectedLength then
                Error PrivateFileFailure.Unreadable
            else
                readBytes (readExactly handle) (Array.zeroCreate<byte>(int length))

    let private validPath (path: string) =
        not (path.Contains '\000')
        && (try
                UTF8Encoding(false, true).GetByteCount path |> ignore
                true
            with :? EncoderFallbackException ->
                false)

    let private openRead maximumBytes path =
        let info =
            match File.ResolveLinkTarget(path, true) with
            | null -> FileInfo(path)
            | target -> FileInfo(target.FullName)

        if not info.Exists then
            if Directory.Exists path then
                Error PrivateFileFailure.NotRegular
            else
                Error PrivateFileFailure.Missing
        elif info.Length = 0L then
            Error PrivateFileFailure.NotRegular
        elif info.Length > int64 maximumBytes then
            Error PrivateFileFailure.TooLarge
        else
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read)
            |> readHandle maximumBytes info.Length

    /// The caller owns the returned bytes and clears them after use. Final link targets are followed.
    let internal readOnPlatform supported maximumBytes (path: string) =
        if not supported then
            Error PrivateFileFailure.UnsupportedPlatform
        elif maximumBytes < 1 then
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
            | :? UnauthorizedAccessException
            | :? NotSupportedException -> Error PrivateFileFailure.Unreadable

    let read maximumBytes path =
        readOnPlatform (not (OperatingSystem.IsWindows())) maximumBytes path
