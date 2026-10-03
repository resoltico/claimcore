open System
open System.Runtime.InteropServices
open ClaimCore.HostSecurity

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type ProbeLock = delegate of string -> int

let arguments = fsi.CommandLineArgs
let library = NativeLibrary.Load(arguments[2])

NativeLibrary.SetDllImportResolver(
    typeof<PrivateFileFailure>.Assembly,
    DllImportResolver(fun name _ _ ->
        if name = "claimcore_hostsecurity_native" then
            library
        else
            nativeint 0)
)

let probe =
    Marshal.GetDelegateForFunctionPointer<ProbeLock>(
        NativeLibrary.GetExport(library, "cc_probe_lock")
    )

if not (GC.TryStartNoGCRegion(10485760L)) then
    failwith "The isolated no-GC control could not start."

let refused = PrivateFileService.openExclusive arguments[1] |> Result.isError
let released = probe.Invoke(arguments[1]) = 0
GC.EndNoGCRegion()
if refused && released then exit 0 else exit 1
