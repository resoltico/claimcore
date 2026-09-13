namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

module internal PosixPrivateNative =
    [<DllImport("libc", EntryPoint = "openat", SetLastError = true)>]
    extern int private openAt(int directory, string name, int flags, int mode)

    [<DllImport("libc", EntryPoint = "fstat", SetLastError = true)>]
    extern int private fileStat(int descriptor, byte[] buffer)

    [<DllImport("libc", EntryPoint = "fstatat", SetLastError = true)>]
    extern int private pathStat(int directory, string name, byte[] buffer, int flags)

    [<DllImport("libc", EntryPoint = "fsync", SetLastError = true)>]
    extern int private sync(int descriptor)

    [<DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)>]
    extern int private unlinkAt(int directory, string name, int flags)

    [<DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)>]
    extern int private mkdirAt(int directory, string name, int mode)

    [<DllImport("libc", EntryPoint = "geteuid")>]
    extern uint32 private effectiveUserId()

    [<DllImport("libc", EntryPoint = "acl_get_fd_np", SetLastError = true)>]
    extern nativeint private getAcl(int descriptor, int aclType)

    [<DllImport("libc", EntryPoint = "acl_free", SetLastError = true)>]
    extern int private freeAcl(nativeint acl)

    [<DllImport("claimcore_hostsecurity_native", EntryPoint = "cc_private_abi_version")>]
    extern int private shimVersion()

    [<DllImport("claimcore_hostsecurity_native",
                EntryPoint = "cc_openat_create",
                SetLastError = true)>]
    extern int private shimCreate(int parent, string leaf)

    [<DllImport("claimcore_hostsecurity_native", EntryPoint = "cc_openat_lock", SetLastError = true)>]
    extern int private shimLock(int parent, string leaf, int& created)

    [<NoEquality; NoComparison>]
    type Identity =
        {
            Device: uint64
            Inode: uint64
            Owner: uint32
            Mode: uint32
        }

    [<NoEquality; NoComparison>]
    type NativeFlags =
        {
            Directory: int
            NoFollow: int
            CloseOnExec: int
            NonBlock: int
            AtNoFollow: int
            AtRemoveDirectory: int
        }

    let flags () =
        if OperatingSystem.IsMacOS() then
            {
                Directory = 0x00100000
                NoFollow = 0x00000100
                CloseOnExec = 0x01000000
                NonBlock = 0x00000004
                AtNoFollow = 0x0020
                AtRemoveDirectory = 0x0080
            }
        elif OperatingSystem.IsLinux() then
            {
                Directory = 0x00010000
                NoFollow = 0x00020000
                CloseOnExec = 0x00080000
                NonBlock = 0x00000800
                AtNoFollow = 0x0100
                AtRemoveDirectory = 0x0200
            }
        else
            raise (PlatformNotSupportedException("Private files require supported POSIX APIs."))

    let private identity (buffer: byte array) =
        if not BitConverter.IsLittleEndian then
            raise (PlatformNotSupportedException("Private-file metadata ABI is unsupported."))

        let modeOffset, ownerOffset =
            if OperatingSystem.IsMacOS() then
                4, 16
            elif
                OperatingSystem.IsLinux()
                && RuntimeInformation.ProcessArchitecture = Architecture.X64
            then
                24, 28
            elif
                OperatingSystem.IsLinux()
                && RuntimeInformation.ProcessArchitecture = Architecture.Arm64
            then
                16, 24
            else
                raise (PlatformNotSupportedException("Private-file metadata ABI is unsupported."))

        {
            Device =
                if OperatingSystem.IsMacOS() then
                    uint64 (BitConverter.ToUInt32(buffer, 0))
                else
                    BitConverter.ToUInt64(buffer, 0)
            Inode = BitConverter.ToUInt64(buffer, 8)
            Owner = BitConverter.ToUInt32(buffer, ownerOffset)
            Mode =
                if OperatingSystem.IsMacOS() then
                    uint32 (BitConverter.ToUInt16(buffer, modeOffset))
                else
                    BitConverter.ToUInt32(buffer, modeOffset)
        }

    let stat (descriptor: nativeint) =
        let buffer = Array.zeroCreate<byte> 256

        if fileStat (int descriptor, buffer) <> 0 then
            raise (IOException("Private-file descriptor metadata is unavailable."))

        identity buffer

    let statEntry directory name =
        let buffer = Array.zeroCreate<byte> 256

        if pathStat (directory, name, buffer, (flags ()).AtNoFollow) <> 0 then
            None
        else
            Some(identity buffer)

    let sameFile left right =
        left.Device = right.Device && left.Inode = right.Inode

    let private hasNoExtendedAcl descriptor =
        if not (OperatingSystem.IsMacOS()) then
            true
        else
            let acl = getAcl (descriptor, 0x00000100)

            if acl = nativeint 0 then
                // Darwin reports an absent extended ACL as ENOENT.
                Marshal.GetLastPInvokeError() = 2
            else
                freeAcl acl |> ignore
                false

    let ownerPrivateRegular info =
        info.Mode &&& 0o170000u = 0o100000u
        && info.Owner = effectiveUserId ()
        && info.Mode &&& 0o077u = 0u

    let privateDirectory (descriptor: nativeint) =
        let info = stat descriptor

        if
            info.Mode &&& 0o170000u <> 0o040000u
            || info.Owner <> effectiveUserId ()
            || info.Mode &&& 0o777u <> 0o700u
            || not (hasNoExtendedAcl (int descriptor))
        then
            raise (UnauthorizedAccessException("Private directory is not owner-private."))

        info

    let privateRegular (descriptor: nativeint) =
        let info = stat descriptor

        if not (ownerPrivateRegular info && hasNoExtendedAcl (int descriptor)) then
            raise (UnauthorizedAccessException("Private file is not owner-private and regular."))

        info

    let safeDirectory (descriptor: nativeint) =
        let info = stat descriptor
        let stickyRoot = info.Owner = 0u && info.Mode &&& 0o1000u <> 0u

        if
            info.Mode &&& 0o170000u <> 0o040000u
            || (info.Owner <> 0u && info.Owner <> effectiveUserId ())
            || (info.Mode &&& 0o022u <> 0u && not stickyRoot)
            || not (hasNoExtendedAcl (int descriptor))
        then
            raise (UnauthorizedAccessException("Private-file ancestor is unsafe."))

    let openHandle directory name openFlags =
        // No O_CREAT here: Darwin arm64 uses a different variadic calling convention.
        let descriptor = openAt (directory, name, openFlags, 0)

        if descriptor < 0 then
            raise (IOException("Private-file path cannot be opened safely."))

        new SafeFileHandle(nativeint descriptor, true)

    let verifyShim () =
        if shimVersion () <> 1 then
            raise (PlatformNotSupportedException("Private-file native ABI is unsupported."))

    let createPrivateHandle parent name =
        verifyShim ()
        let descriptor = shimCreate (parent, name)

        if descriptor < 0 then
            raise (IOException("Private file could not be created safely."))

        new SafeFileHandle(nativeint descriptor, true)

    let openLockedHandle parent name =
        verifyShim ()
        let mutable created = 0
        let descriptor = shimLock (parent, name, &created)

        if descriptor < 0 then
            raise (IOException("Private lock could not be acquired safely."))

        new SafeFileHandle(nativeint descriptor, true), created = 1

    let createDirectory parent name =
        if mkdirAt (parent, name, 0o700) = 0 then
            true
        elif Marshal.GetLastPInvokeError() = 17 then
            false
        else
            raise (IOException("Private directory could not be created."))

    let unlink parent name directory =
        let flags = if directory then (flags ()).AtRemoveDirectory else 0

        if unlinkAt (parent, name, flags) <> 0 then
            raise (IOException("Private file could not be removed safely."))

    let syncDirectory descriptor =
        if sync (descriptor) <> 0 then
            raise (IOException("Private directory durability could not be confirmed."))
