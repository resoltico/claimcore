namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open Microsoft.Win32.SafeHandles

/// Why an owner-private file was refused. Values carry no path and no content.
[<RequireQualifiedAccess>]
type PrivateFileFailure =
    | NotAbsolute
    | InvalidPath
    | InvalidLimit
    | Missing
    | IsLink
    | NotRegular
    | WrongOwner
    | TooPermissive
    | TooLarge
    | Unreadable
    | AclRefused
    | NativeAssetUnavailable
    | UnsupportedPlatform

module internal PrivateFileNative =
    [<DllImport("claimcore_hostsecurity",
                EntryPoint = "claimcore_private_file_open",
                CallingConvention = CallingConvention.Cdecl)>]
    extern int private openFile([<MarshalAs(UnmanagedType.LPUTF8Str)>] string path, int& descriptor)

    let status =
        function
        | 0 -> Ok()
        | 1 -> Error PrivateFileFailure.Missing
        | 2 -> Error PrivateFileFailure.IsLink
        | 3 -> Error PrivateFileFailure.NotRegular
        | 4 -> Error PrivateFileFailure.WrongOwner
        | 5 -> Error PrivateFileFailure.TooPermissive
        | 6 -> Error PrivateFileFailure.Unreadable
        | 7 -> Error PrivateFileFailure.AclRefused
        | _ -> Error PrivateFileFailure.Unreadable

    let invoke action =
        try
            action ()
        with
        | :? DllNotFoundException
        | :? EntryPointNotFoundException
        | :? BadImageFormatException -> Error PrivateFileFailure.NativeAssetUnavailable

    let openRead path =
        invoke (fun () ->
            let mutable descriptor = -1

            match status (openFile (path, &descriptor)) with
            | Ok() when descriptor >= 0 -> Ok descriptor
            | Ok() -> Error PrivateFileFailure.Unreadable
            | Error failure -> Error failure)

/// Same-descriptor admission and bounded reads of owner-private regular files.
module PrivateFile =
    /// Fixed, nonsecret guidance for a missing or incompatible native runtime asset.
    let nativeAssetGuidance =
        "Private-file native support is missing or incompatible. Rebuild or reinstall ClaimCore for this supported operating system and architecture."

    let private readOpened maximumBytes (stream: FileStream) =
        let length = if stream.CanSeek then stream.Length else 0L

        if not stream.CanSeek then
            Error PrivateFileFailure.NotRegular
        elif length > int64 maximumBytes then
            Error PrivateFileFailure.TooLarge
        else
            let buffer = Array.zeroCreate<byte>(int length)

            try
                stream.ReadExactly(buffer, 0, buffer.Length)
                Ok buffer
            with _ ->
                CryptographicOperations.ZeroMemory(Span<byte> buffer)
                reraise ()

    let internal readHandle maximumBytes (handle: SafeFileHandle) =
        let opened =
            try
                new FileStream(handle, FileAccess.Read, 1, false)
            with _ ->
                handle.Dispose()
                reraise ()

        use stream = opened
        readOpened maximumBytes stream

    let private validPath (path: string) =
        if path.Contains '\000' then
            false
        else
            try
                UTF8Encoding(false, true).GetByteCount path |> ignore
                true
            with :? EncoderFallbackException ->
                false

    /// Reads at most `maximumBytes`. The caller owns the returned buffer and clears it after use.
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
                match PrivateFileNative.openRead path with
                | Error failure -> Error failure
                | Ok descriptor ->
                    readHandle maximumBytes (new SafeFileHandle(nativeint descriptor, true))
            with
            | :? IOException
            | :? UnauthorizedAccessException -> Error PrivateFileFailure.Unreadable

    let read maximumBytes path =
        let supportedArchitecture =
            RuntimeInformation.ProcessArchitecture = Architecture.Arm64
            || RuntimeInformation.ProcessArchitecture = Architecture.X64

        readOnPlatform
            ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
             && supportedArchitecture)
            maximumBytes
            path
