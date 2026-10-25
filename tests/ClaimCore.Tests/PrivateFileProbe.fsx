open System
open System.Runtime.InteropServices
open ClaimCore.HostSecurity

[<DllImport("libc", EntryPoint = "close")>]
extern int closeDescriptor(int descriptor)

[<DllImport("libc", EntryPoint = "fcntl", SetLastError = true)>]
extern int descriptorFlags(int descriptor, int command)

// Isolated subprocess: never close the test runner's standard input.
match fsi.CommandLineArgs[1] with
| "zero" ->
    closeDescriptor 0 |> ignore
    let result = PrivateFile.read 1 fsi.CommandLineArgs[2]

    if result <> Ok [| 42uy |] then
        exit 1

    if descriptorFlags (0, 1) <> -1 || Marshal.GetLastPInvokeError() <> 9 then
        exit 2
| "refusals" ->
    let count () =
        System.IO.Directory.GetFileSystemEntries("/dev/fd").Length
    // Warm up runtime marshalling before measuring owned repeated refusals.
    PrivateFile.read 1 fsi.CommandLineArgs[2] |> ignore
    let before = count ()
    let threads = System.Threading.ThreadPool.ThreadCount

    let watch = System.Diagnostics.Stopwatch.StartNew()

    for _ in 1..1000 do
        if PrivateFile.read 1 fsi.CommandLineArgs[2] <> Error PrivateFileFailure.NotRegular then
            exit 3

    if watch.Elapsed > TimeSpan.FromSeconds 3. then
        exit 7

    if count () <> before then
        exit 4

    if System.Threading.ThreadPool.ThreadCount <> threads then
        exit 5
| _ -> exit 6

exit 0
