namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Security.Cryptography

/// Why an owner-private file was refused. Values carry no path and no content.
[<RequireQualifiedAccess>]
type PrivateFileFailure =
    | NotAbsolute
    | Missing
    | IsLink
    | NotRegular
    | TooPermissive
    | TooLarge
    | Unreadable
    | UnsupportedPlatform

/// Reads owner-private regular files using a bounded Unix type probe and handle-based modes.
///
/// The rule is the one OpenSSH applies to a private key: a regular file that nobody but its
/// owner can access. The mode is read from the open handle, so it describes the bytes that are
/// read. A file with no group or other access that this process could open belongs to this user
/// (or the process is root), so no separate owner comparison is needed.
module PrivateFile =
    let private groupOrOther =
        UnixFileMode.GroupRead
        ||| UnixFileMode.GroupWrite
        ||| UnixFileMode.GroupExecute
        ||| UnixFileMode.OtherRead
        ||| UnixFileMode.OtherWrite
        ||| UnixFileMode.OtherExecute

    let private regularFile path =
        let start = Diagnostics.ProcessStartInfo("stat")
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        start.Environment["LC_ALL"] <- "C"

        for argument in
            (if OperatingSystem.IsMacOS() then
                 [ "-f"; "%HT"; path ]
             else
                 [ "-c"; "%F"; path ]) do
            start.ArgumentList.Add argument

        use child = Diagnostics.Process.Start start |> nonNull

        if not (child.WaitForExit 2000) then
            child.Kill(true)
            child.WaitForExit()
            Error PrivateFileFailure.Unreadable
        elif child.ExitCode <> 0 then
            Error PrivateFileFailure.Unreadable
        else
            match child.StandardOutput.ReadToEnd().Trim() with
            | "Regular File"
            | "regular file"
            | "regular empty file" -> Ok()
            | _ -> Error PrivateFileFailure.NotRegular

    let private readOpened (maximumBytes: int) (stream: FileStream) =
        let length = if stream.CanSeek then stream.Length else 0L

        if not stream.CanSeek then
            Error PrivateFileFailure.NotRegular
        elif File.GetUnixFileMode stream.SafeFileHandle &&& groupOrOther <> UnixFileMode.None then
            Error PrivateFileFailure.TooPermissive
        elif
            File.GetUnixFileMode stream.SafeFileHandle &&& UnixFileMode.UserRead = UnixFileMode.None
        then
            Error PrivateFileFailure.Unreadable
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

    /// Reads at most `maximumBytes`. The caller owns the returned buffer and clears it after use.
    let internal readOnPlatform
        supported
        (maximumBytes: int)
        (path: string)
        : Result<byte array, PrivateFileFailure> =
        if not supported then
            Error PrivateFileFailure.UnsupportedPlatform
        elif String.IsNullOrEmpty path || not (Path.IsPathFullyQualified path) then
            Error PrivateFileFailure.NotAbsolute
        else
            try
                let info = FileInfo path

                if not (isNull info.LinkTarget) then
                    Error PrivateFileFailure.IsLink
                elif Directory.Exists path then
                    Error PrivateFileFailure.NotRegular
                elif not info.Exists then
                    Error PrivateFileFailure.Missing
                else
                    match regularFile path with
                    | Error failure -> Error failure
                    | Ok() ->
                        use stream =
                            new FileStream(
                                path,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.Read,
                                1,
                                FileOptions.None
                            )

                        readOpened maximumBytes stream
            with
            | :? ComponentModel.Win32Exception
            | :? IOException
            | :? UnauthorizedAccessException -> Error PrivateFileFailure.Unreadable

    let read maximumBytes path =
        readOnPlatform (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) maximumBytes path
