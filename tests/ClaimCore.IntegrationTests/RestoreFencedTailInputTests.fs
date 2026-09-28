module ClaimCore.IntegrationTests.RestoreFencedTailInputTests

open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Database
open ClaimCore.HostSecurity

let private privateRoot () =
    let temporary = Path.GetTempPath()

    let physical =
        if
            OperatingSystem.IsMacOS()
            && temporary.StartsWith("/var/", StringComparison.Ordinal)
        then
            "/private" + temporary
        else
            temporary

    let root =
        Path.Combine(physical, "claimcore-fenced-input-" + Guid.NewGuid().ToString("N"))

    let mode =
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute

    Directory.CreateDirectory(root, mode) |> ignore
    root

let private file root name bytes =
    let path = Path.Combine(root, name)

    match PrivateFileService.writeNew 256 path bytes with
    | Ok() -> path
    | Error _ -> failtest "Owner-private fenced-tail test file could not be created"

let private paths root =
    let content = Encoding.ASCII.GetBytes("{}\n")
    let signature = Array.zeroCreate<byte> 64

    {
        Report = file root "report.json" content
        ReportSignature = file root "report.sig" signature
        EvidenceIndex = file root "index.json" content
        Fence = file root "fence.json" content
        FenceSignature = file root "fence.sig" signature
        Supplement = file root "supplement.json" content
        SupplementSignature = file root "supplement.sig" signature
    }

let tests =
    testList
        "fenced-tail owner private inputs"
        [
            testCase
                "[CC-BACKUP-001] owner final-tail evidence refuses a symlink and missing signature"
                (fun _ ->
                    let root = privateRoot ()

                    try
                        let valid = paths root
                        let loaded = DatabaseFencedTailInputs.load valid
                        DatabaseFencedTailInputs.dispose loaded

                        let alias = Path.Combine(root, "supplement-alias.json")
                        File.CreateSymbolicLink(alias, valid.Supplement) |> ignore

                        Expect.throws
                            (fun () ->
                                DatabaseFencedTailInputs.load { valid with Supplement = alias }
                                |> DatabaseFencedTailInputs.dispose)
                            "A symlink cannot supply signed final-tail bytes"

                        Expect.throws
                            (fun () ->
                                DatabaseFencedTailInputs.load
                                    { valid with
                                        SupplementSignature = Path.Combine(root, "absent.sig")
                                    }
                                |> DatabaseFencedTailInputs.dispose)
                            "Missing detached final-tail signature refuses"
                    finally
                        Directory.Delete(root, true))
        ]
