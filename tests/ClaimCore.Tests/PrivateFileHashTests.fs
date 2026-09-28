module ClaimCore.Tests.PrivateFileHashTests

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open Expecto
open ClaimCore.HostSecurity

let private physicalTemp () =
    let temporary = Path.GetTempPath()

    if
        OperatingSystem.IsMacOS()
        && (temporary.StartsWith("/var/", StringComparison.Ordinal)
            || temporary.StartsWith("/tmp/", StringComparison.Ordinal))
    then
        "/private" + temporary
    else
        temporary

let private sandbox action =
    let path =
        Path.Combine(physicalTemp (), "claimcore-private-hash-" + Guid.NewGuid().ToString("N"))

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(path, mode) |> ignore

    try
        action path
    finally
        Directory.Delete(path, true)

let private write path (bytes: byte array) =
    let options =
        FileStreamOptions(Mode = FileMode.CreateNew, Access = FileAccess.Write)

    options.UnixCreateMode <- Nullable(UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
    use stream = new FileStream(path, options)
    stream.Write(ReadOnlySpan<byte>(bytes))
    stream.Flush(true)

[<DllImport("libc", EntryPoint = "link", SetLastError = true)>]
extern int private link(string source, string destination)

let private refused result =
    match result with
    | Error _ -> ()
    | Ok _ -> failtest "Unsafe private file was hashed"

let private boundaries =
    testCase "[CC-BACKUP-001] private archive hash is bounded and rejects aliases" (fun _ ->
        if OperatingSystem.IsWindows() then
            PrivateFileService.hashPrivateFile 1024L "C:\\synthetic\\archive.age" |> refused
        else
            sandbox (fun directory ->
                let content = RandomNumberGenerator.GetBytes(262144)
                let source = Path.Combine(directory, "source.age")
                write source content

                match PrivateFileService.hashPrivateFile (int64 content.Length) source with
                | Ok(length, hash) ->
                    Expect.equal length (int64 content.Length) "Exact streamed length"

                    Expect.equal
                        hash
                        (SHA256.HashData(ReadOnlySpan<byte>(content)))
                        "Exact streamed digest"
                | Error _ -> failtest "Private source hash was refused"

                PrivateFileService.hashPrivateFile (int64 content.Length - 1L) source |> refused

                let alias = Path.Combine(directory, "alias.age")
                File.CreateSymbolicLink(alias, source) |> ignore
                PrivateFileService.hashPrivateFile 262144L alias |> refused

                let hardlink = Path.Combine(directory, "hardlink.age")
                Expect.equal (link (source, hardlink)) 0 "Synthetic hardlink was created"
                PrivateFileService.hashPrivateFile 262144L source |> refused
                PrivateFileService.hashPrivateFile 262144L hardlink |> refused

                File.Delete(hardlink)
                let changed = RandomNumberGenerator.GetBytes(content.Length)
                File.WriteAllBytes(source, changed)

                match PrivateFileService.hashPrivateFile (int64 changed.Length) source with
                | Ok(length, hash) ->
                    Expect.equal length (int64 changed.Length) "Changed file length is checked"

                    Expect.equal
                        hash
                        (SHA256.HashData(ReadOnlySpan<byte>(changed)))
                        "Changed bytes change the digest"
                | Error _ -> failtest "Changed private source was not hashable"))

let tests = testList "private archive hashing" [ boundaries ]
