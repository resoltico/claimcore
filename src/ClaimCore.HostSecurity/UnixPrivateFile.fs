namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

/// System-library ABI only; platform-header derivation is documented in docs/development.md.
module internal UnixPrivateFile =
    type Abi =
        | DarwinIntel
        | DarwinArm
        | GlibcIntel
        | GlibcArm

    module private Darwin =
        [<DllImport("libSystem.B.dylib", EntryPoint = "open", SetLastError = true)>]
        extern int openFile(byte[] path, int flags)

        [<DllImport("libSystem.B.dylib", EntryPoint = "fstat", SetLastError = true)>]
        extern int statFile(SafeHandle handle, [<Out>] byte[] buffer)

        [<DllImport("libSystem.B.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)>]
        extern int statIntel(SafeHandle handle, [<Out>] byte[] buffer)

        [<DllImport("libSystem.B.dylib", EntryPoint = "read", SetLastError = true)>]
        extern nativeint readFile(SafeHandle handle, nativeint buffer, unativeint length)

        [<DllImport("libSystem.B.dylib", EntryPoint = "close", SetLastError = true)>]
        extern int closeFile(nativeint handle)

    module private Glibc =
        [<DllImport("libc.so.6", EntryPoint = "open", SetLastError = true)>]
        extern int openFile(byte[] path, int flags)

        [<DllImport("libc.so.6", EntryPoint = "fstat", SetLastError = true)>]
        extern int statFile(SafeHandle handle, [<Out>] byte[] buffer)

        [<DllImport("libc.so.6", EntryPoint = "read", SetLastError = true)>]
        extern nativeint readFile(SafeHandle handle, nativeint buffer, unativeint length)

        [<DllImport("libc.so.6", EntryPoint = "close", SetLastError = true)>]
        extern int closeFile(nativeint handle)

        [<DllImport("libc.so.6", EntryPoint = "gnu_get_libc_version")>]
        extern nativeint version()

    let private isDarwin abi = abi = DarwinIntel || abi = DarwinArm

    type Handle(abi, descriptor) =
        inherit SafeHandleMinusOneIsInvalid(true)
        do base.SetHandle(nativeint descriptor)
        member _.Abi = abi
        // Never retry close: after EINTR the descriptor may already have been reused.
        override this.ReleaseHandle() =
            let close = if isDarwin abi then Darwin.closeFile else Glibc.closeFile
            close this.handle = 0

    let private glibc () =
        let mutable library = 0n

        try
            try
                if
                    RuntimeInformation.RuntimeIdentifier.StartsWith(
                        "linux-musl",
                        StringComparison.Ordinal
                    )
                    || not (NativeLibrary.TryLoad("libc.so.6", &library))
                then
                    false
                else
                    let mutable symbol = 0n
                    NativeLibrary.TryGetExport(library, "fstat", &symbol) && Glibc.version () <> 0n
            with
            | :? DllNotFoundException
            | :? EntryPointNotFoundException -> false
        finally
            if library <> 0n then
                NativeLibrary.Free library

    let platform () =
        match RuntimeInformation.ProcessArchitecture with
        | _ when not BitConverter.IsLittleEndian -> None
        | Architecture.X64 when OperatingSystem.IsMacOS() -> Some DarwinIntel
        | Architecture.Arm64 when OperatingSystem.IsMacOS() -> Some DarwinArm
        | Architecture.X64 when OperatingSystem.IsLinux() && glibc () -> Some GlibcIntel
        | Architecture.Arm64 when OperatingSystem.IsLinux() && glibc () -> Some GlibcArm
        | _ -> None

    let private error () =
        match Marshal.GetLastPInvokeError() with
        | 2
        | 20 -> raise (FileNotFoundException("Private input unavailable."))
        | _ -> raise (IOException("Private input unavailable."))

    let rec internal interrupted call =
        let result = call ()

        if result = -1 && Marshal.GetLastPInvokeError() = 4 then
            interrupted call
        else
            result

    let openRead abi path =
        let flags =
            match abi with
            | DarwinIntel
            | DarwinArm -> 0x01000004
            | GlibcIntel
            | GlibcArm -> 0x80800

        let openFile = if isDarwin abi then Darwin.openFile else Glibc.openFile
        let descriptor = interrupted (fun () -> openFile (path, flags))

        if descriptor < 0 then
            error ()

        new Handle(abi, descriptor)

    let metadata abi (handle: SafeHandle) =
        let size, modeOffset, sizeOffset =
            match abi with
            | DarwinIntel
            | DarwinArm -> 144, 4, 96
            | GlibcIntel -> 144, 24, 48
            | GlibcArm -> 128, 16, 48

        let buffer = Array.zeroCreate<byte> size

        let stat =
            match abi with
            | DarwinIntel -> Darwin.statIntel
            | DarwinArm -> Darwin.statFile
            | GlibcIntel
            | GlibcArm -> Glibc.statFile

        if interrupted (fun () -> stat (handle, buffer)) <> 0 then
            error ()

        let mode =
            if abi = DarwinIntel || abi = DarwinArm then
                int (BitConverter.ToUInt16(buffer, modeOffset))
            else
                BitConverter.ToInt32(buffer, modeOffset)

        mode, BitConverter.ToInt64(buffer, sizeOffset)

    let read (handle: Handle) (buffer: byte array) =
        let readFile =
            if isDarwin handle.Abi then
                Darwin.readFile
            else
                Glibc.readFile

        let pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned)

        try
            let mutable offset = 0

            while offset < buffer.Length do
                let count =
                    interrupted (fun () ->
                        int (
                            readFile (
                                handle,
                                pinned.AddrOfPinnedObject() + nativeint offset,
                                unativeint (buffer.Length - offset)
                            )
                        ))

                if count < 0 then
                    error ()

                if count = 0 then
                    raise (EndOfStreamException("Private input changed while reading."))

                offset <- offset + count
        finally
            pinned.Free()
