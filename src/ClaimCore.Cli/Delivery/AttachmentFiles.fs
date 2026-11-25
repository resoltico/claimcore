namespace ClaimCore.Cli

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Microsoft.Win32.SafeHandles
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type internal AttachmentSource =
    {
        Name: string
        Bytes: byte array
        Digest: AttachmentDigest
    }

[<RequireQualifiedAccess>]
type internal AttachmentCaptureFailure =
    | TooLarge of actualBytes: int64
    | Unreadable
    | Changed

module internal AttachmentFiles =
    // The shipped .NET Unix runtime supplies a common stat layout across glibc, musl and Darwin.
    // See src/native/libs/System.Native/pal_io.h in the runtime source.
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private FileStatus =
        val Flags: int
        val Mode: int
        val Uid: uint32
        val Gid: uint32
        val Size: int64
        val ATime: int64
        val ATimeNsec: int64
        val MTime: int64
        val MTimeNsec: int64
        val CTime: int64
        val CTimeNsec: int64
        val BirthTime: int64
        val BirthTimeNsec: int64
        val Dev: int64
        val RDev: int64
        val Ino: int64
        val UserFlags: uint32
        val HardLinkCount: uint32

    [<DllImport("System.Native", EntryPoint = "SystemNative_FStat")>]
    extern int private status(SafeFileHandle handle, FileStatus& result)

    [<DllImport("libc", EntryPoint = "open")>]
    extern int private openFile([<MarshalAs(UnmanagedType.LPUTF8Str)>] string path, int flags)

    [<DllImport("libc", EntryPoint = "link", SetLastError = true)>]
    extern int private publish(
        [<MarshalAs(UnmanagedType.LPUTF8Str)>] string existing,
        [<MarshalAs(UnmanagedType.LPUTF8Str)>] string destination
    )

    [<DllImport("libc", EntryPoint = "openat")>]
    extern int private openRelative(
        SafeFileHandle directory,
        [<MarshalAs(UnmanagedType.LPUTF8Str)>] string name,
        int flags
    )

    [<DllImport("libc", EntryPoint = "unlinkat")>]
    extern int private unlinkRelative(
        SafeFileHandle directory,
        [<MarshalAs(UnmanagedType.LPUTF8Str)>] string name,
        int flags
    )

    [<DllImport("libc", EntryPoint = "mkdtemp")>]
    extern nativeint private makeTemporaryDirectory(nativeint template)

    [<DllImport("libc", EntryPoint = "geteuid")>]
    extern uint32 private effectiveUser()

    let private nonblocking = if OperatingSystem.IsMacOS() then 0x1000004 else 0x80800

    let private directoryFlag, noFollow =
        if OperatingSystem.IsMacOS() then
            0x100000, 0x100
        else
            // Linux ARM uses its original fcntl ABI for these flags on both architectures.
            match RuntimeInformation.ProcessArchitecture with
            | Architecture.Arm
            | Architecture.Arm64 -> 0x4000, 0x8000
            | _ -> 0x10000, 0x20000

    let private directoryHandle path =
        new SafeFileHandle(nativeint (openFile (path, nonblocking ||| directoryFlag)), true)

    let private protectedDirectory (handle: SafeFileHandle) =
        let mutable state = Unchecked.defaultof<FileStatus>

        not handle.IsInvalid
        && status (handle, &state) = 0
        && (state.Uid = 0u || state.Uid = effectiveUser ())
        && (state.Mode &&& 0o022 = 0 || state.Mode &&& 0o1000 <> 0)

    let private sameVersion (before: FileStatus) (after: FileStatus) =
        before.Size = after.Size
        && before.MTime = after.MTime
        && before.MTimeNsec = after.MTimeNsec
        && before.CTime = after.CTime
        && before.CTimeNsec = after.CTimeNsec

    let capture path =
        try
            let path = Path.GetFullPath path

            let descriptor = openFile (path, nonblocking)

            if descriptor < 0 then
                Error AttachmentCaptureFailure.Unreadable
            else
                use handle = new SafeFileHandle(nativeint descriptor, true)
                let mutable before = Unchecked.defaultof<FileStatus>

                if
                    status (handle, &before) <> 0
                    || before.Mode &&& 0xF000 <> 0x8000
                    || before.Size < 0L
                then
                    Error AttachmentCaptureFailure.Unreadable
                elif before.Size > int64 AttachmentLimits.FileBytes then
                    Error(AttachmentCaptureFailure.TooLarge before.Size)
                else
                    use source = new FileStream(handle, FileAccess.Read)
                    let bytes = GC.AllocateUninitializedArray<byte>(int before.Size)
                    source.ReadExactly(bytes.AsSpan())
                    let extra = source.ReadByte()
                    let mutable after = Unchecked.defaultof<FileStatus>

                    if status (handle, &after) <> 0 then
                        Error AttachmentCaptureFailure.Unreadable
                    elif extra <> -1 || not (sameVersion before after) then
                        Error AttachmentCaptureFailure.Changed
                    else
                        Ok
                            {
                                Name = Path.GetFileName path
                                Bytes = bytes
                                Digest = AttachmentDigest.hash (ReadOnlySpan<byte> bytes)
                            }
        with
        | :? EndOfStreamException -> Error AttachmentCaptureFailure.Changed
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error AttachmentCaptureFailure.Unreadable

    let private create path =
        new FileStream(
            path,
            FileStreamOptions(
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = (UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
            )
        )

    let private write (output: FileStream) (bytes: byte array) =
        output.Write bytes
        output.Flush(true)

    let private removeIncomplete directory name (owned: SafeFileHandle) =
        // A pinned directory prevents cleanup from following a replaced parent path.
        // In a directory where other users can replace our entry, leave it alone.
        if protectedDirectory directory then
            use current =
                new SafeFileHandle(
                    nativeint (openRelative (directory, name, nonblocking ||| noFollow)),
                    true
                )

            let mutable original = Unchecked.defaultof<FileStatus>
            let mutable entry = Unchecked.defaultof<FileStatus>

            if
                not current.IsInvalid
                && status (owned, &original) = 0
                && status (current, &entry) = 0
                && original.Uid = effectiveUser ()
                && (original.Dev, original.Ino) = (entry.Dev, entry.Ino)
            then
                unlinkRelative (directory, name, 0) |> ignore

    let private saveExclusive writeExclusive (target: string) bytes (cancelled: CancellationToken) =
        cancelled.ThrowIfCancellationRequested()
        use directory = directoryHandle (Path.GetDirectoryName target |> nonNull)
        use output = create target
        let owned = output.SafeFileHandle

        try
            // The exclusive create is the publication point on this filesystem.
            // Complete the bounded write once that point has passed.
            writeExclusive output bytes
            Ok()
        with _ ->
            removeIncomplete directory (Path.GetFileName target) owned
            reraise ()

    let private stagingDirectory () =
        // Keep the source pathname in an OS temporary directory protected by ownership or
        // the sticky bit. A third party able to write the chosen destination cannot replace it.
        let root = if OperatingSystem.IsMacOS() then "/private/tmp" else "/tmp"
        use handle = directoryHandle root

        if not (protectedDirectory handle) then
            None
        else
            let template = Marshal.StringToCoTaskMemUTF8(Path.Combine(root, "claimcore-XXXXXX"))

            try
                if makeTemporaryDirectory template = 0n then
                    None
                else
                    let path = Marshal.PtrToStringUTF8 template |> nonNull
                    use stage = directoryHandle path
                    let mutable state = Unchecked.defaultof<FileStatus>

                    if
                        not stage.IsInvalid
                        && status (stage, &state) = 0
                        && state.Uid = effectiveUser ()
                        && state.Mode &&& 0o077 = 0
                    then
                        Some path
                    else
                        Directory.Delete path
                        None
            finally
                Marshal.FreeCoTaskMem template

    let private canUseExclusiveCreate error =
        // EXDEV, Linux EOPNOTSUPP, Darwin ENOTSUP and Darwin EOPNOTSUPP.
        error = 18
        || (if OperatingSystem.IsMacOS() then
                error = 45 || error = 102
            else
                error = 95)

    let saveWith writeExclusive path (bytes: byte array) (cancelled: CancellationToken) =
        try
            cancelled.ThrowIfCancellationRequested()
            let target = Path.GetFullPath path

            match stagingDirectory () with
            | None -> Error()
            | Some directory ->
                let temporary = Path.Combine(directory, "payload")

                let linked =
                    try
                        do
                            use output = create temporary
                            write output bytes

                        cancelled.ThrowIfCancellationRequested()

                        if publish (temporary, target) = 0 then
                            Ok()
                        else
                            Error(Marshal.GetLastPInvokeError())
                    finally
                        File.Delete temporary
                        Directory.Delete directory

                match linked with
                | Ok() -> Ok()
                | Error error when canUseExclusiveCreate error ->
                    saveExclusive writeExclusive target bytes cancelled
                | Error _ -> Error()
        with
        | :? IOException
        | :? UnauthorizedAccessException
        | :? ArgumentException
        | :? NotSupportedException -> Error()

    let save path bytes cancelled = saveWith write path bytes cancelled
