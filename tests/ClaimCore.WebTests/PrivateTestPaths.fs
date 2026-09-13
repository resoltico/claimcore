module ClaimCore.WebTests.PrivateTestPaths

open System
open System.IO

let physicalTemporaryBase () =
    let temporary = Path.GetTempPath()

    if
        OperatingSystem.IsMacOS()
        && (temporary.StartsWith("/var/", StringComparison.Ordinal)
            || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
    then
        "/private" + temporary
    else
        temporary

let newPrivateDirectory prefix =
    let path =
        Path.Combine(physicalTemporaryBase (), prefix + Guid.NewGuid().ToString("N"))

    if OperatingSystem.IsWindows() then
        Directory.CreateDirectory(path) |> ignore
    else
        Directory.CreateDirectory(
            path,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )
        |> ignore

    path
