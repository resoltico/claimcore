namespace ClaimCore.HostSecurity

open System
open System.IO
open System.Runtime.InteropServices
open Microsoft.Win32.SafeHandles

module internal PosixPrivateNative =
    [<DllImport("libc", EntryPoint = "openat", SetLastError = true)>]
    extern int private openAt(int directory, string name, int flags, int mode)

    [<DllImport("claimcore_hostsecurity_native",
                EntryPoint = "cc_private_flags",
                SetLastError = true)>]
    extern int private nativeFlags(int[] values, int count)

    [<DllImport("claimcore_hostsecurity_native",
                EntryPoint = "cc_private_fstat",
                SetLastError = true)>]
    extern int private fileStat(int descriptor, uint64[] values, int count)

    [<DllImport("claimcore_hostsecurity_native",
                EntryPoint = "cc_private_fstatat",
                SetLastError = true)>]
    extern int private pathStat(int directory, string name, uint64[] values, int count)

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
            LinkCount: uint64
            Size: int64
        }

    [<NoEquality; NoComparison>]
    type NativeFlags =
        {
            Directory: int
            NoFollow: int
            CloseOnExec: int
            NonBlock: int
            AtRemoveDirectory: int
        }

    let verifyShim () =
        if shimVersion () <> 2 then
            raise (PlatformNotSupportedException("Private-file native ABI is unsupported."))

    let private platformFlags =
        lazy
            (verifyShim ()
             let values = Array.zeroCreate<int> 5

             if nativeFlags (values, values.Length) <> 0 then
                 raise (IOException("Private-file platform flags are unavailable."))

             {
                 Directory = values[0]
                 NoFollow = values[1]
                 CloseOnExec = values[2]
                 NonBlock = values[3]
                 AtRemoveDirectory = values[4]
             })

    let flags () = platformFlags.Value

    let private identity (values: uint64 array) =
        {
            Device = values[0]
            Inode = values[1]
            Owner = uint32 values[2]
            Mode = uint32 values[3]
            LinkCount = values[4]
            Size = int64 values[5]
        }

    let stat (descriptor: nativeint) =
        let values = Array.zeroCreate<uint64> 6

        if fileStat (int descriptor, values, values.Length) <> 0 then
            raise (IOException("Private-file descriptor metadata is unavailable."))

        identity values

    let statEntry directory name =
        let values = Array.zeroCreate<uint64> 6

        if pathStat (directory, name, values, values.Length) <> 0 then
            None
        else
            Some(identity values)

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
        && info.LinkCount = 1UL

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
