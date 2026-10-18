namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
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

/// Managed admission of bounded private inputs on Linux and macOS.
module PrivateFile =
    let private forbidden =
        UnixFileMode.OtherRead
        ||| UnixFileMode.OtherWrite
        ||| UnixFileMode.OtherExecute
        ||| UnixFileMode.GroupWrite
        ||| UnixFileMode.GroupExecute

    let private admitted mode = mode &&& forbidden = UnixFileMode.None

    let private readOpened maximumBytes (stream: FileStream) =
        if not stream.CanSeek then
            Error PrivateFileFailure.NotRegular
        elif stream.Length > int64 maximumBytes then
            Error PrivateFileFailure.TooLarge
        else
            let buffer = Array.zeroCreate<byte>(int stream.Length)

            try
                stream.ReadExactly(buffer, 0, buffer.Length)
                Ok buffer
            with _ ->
                CryptographicOperations.ZeroMemory(Span<byte> buffer)
                reraise ()

    let internal readHandle maximumBytes (handle: SafeFileHandle) =
        use owned = handle

        if not (admitted (File.GetUnixFileMode handle)) then
            Error PrivateFileFailure.TooPermissive
        else
            use stream = new FileStream(handle, FileAccess.Read, 1, false)
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

    let private abandon (worker: Task<SafeFileHandle>) =
        worker.ContinueWith(fun (finished: Task<SafeFileHandle>) ->
            if finished.IsCompletedSuccessfully then
                finished.Result.Dispose()
            elif finished.IsFaulted then
                finished.Exception |> ignore)
        |> ignore

    let private openRead maximumBytes (path: string) =
        if not (admitted (File.GetUnixFileMode path)) then
            Error PrivateFileFailure.TooPermissive
        elif Directory.Exists path then
            Error PrivateFileFailure.NotRegular
        else
            let worker =
                Task.Run(fun () -> File.OpenHandle(path, FileMode.Open, FileAccess.Read))

            try
                let handle = worker.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
                readHandle maximumBytes handle
            with :? TimeoutException ->
                abandon worker
                Error PrivateFileFailure.NotRegular

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
        readOnPlatform (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) maximumBytes path
