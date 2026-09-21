module ClaimCore.AcceptanceTests.PrivateFileTests

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto

let private ownerFile = UnixFileMode.UserRead ||| UnixFileMode.UserWrite

let private sourceIn directory name bytes =
    let path = Path.Combine(directory, name)
    let options = FileStreamOptions()
    options.Mode <- FileMode.CreateNew
    options.Access <- FileAccess.Write
    options.Share <- FileShare.None
    options.UnixCreateMode <- ownerFile
    use stream = new FileStream(path, options)
    stream.Write(bytes, 0, bytes.Length)
    stream.Flush(true)
    path

let private source (context: DatabaseFixture.Context) name bytes =
    sourceIn context.TemporaryDirectory name bytes

let private noDisclosure (result: ProcessRunner.Result) (path: string) =
    let output = Encoding.UTF8.GetString(result.StandardOutput)
    let diagnostics = Encoding.UTF8.GetString(result.StandardError)
    Expect.isFalse (output.Contains(path, StringComparison.Ordinal)) "Response omits private path"

    Expect.isFalse
        (diagnostics.Contains(path, StringComparison.Ordinal))
        "Diagnostics omit private path"

    Expect.equal diagnostics.Length 0 "Private-file refusal has no diagnostic payload"

let private previewRefused context path =
    let result =
        CliV3Fixtures.importPreview "recovery.importEnvelopePreview" path
        |> CliV3Fixtures.call context

    Expect.equal result.ExitCode 3 "Unsafe source fails locally"
    noDisclosure result path
    use document = JsonDocument.Parse(result.StandardOutput)

    Expect.equal
        (document.RootElement.GetProperty("kind").GetString())
        "protocolFailure"
        "Failure frame"

    Expect.equal
        (document.RootElement.GetProperty("code").GetString())
        "PRIVATE_FILE_ERROR"
        "Failure code"

let private importModeUtf8AndSize () =
    let context = DatabaseFixture.current ()

    let groupReadable =
        source context "group-readable.json" (Encoding.UTF8.GetBytes("synthetic"))

    File.SetUnixFileMode(groupReadable, ownerFile ||| UnixFileMode.GroupRead)
    previewRefused context groupReadable
    let malformed = source context "malformed-utf8.json" [| 0xFFuy |]
    previewRefused context malformed

    let oversized =
        source context "oversized-envelope.json" (Array.create 131073 0x61uy)

    previewRefused context oversized

let private importLinks () =
    let context = DatabaseFixture.current ()

    let target =
        source context "private-target.json" (Encoding.UTF8.GetBytes("synthetic"))

    let link = Path.Combine(context.TemporaryDirectory, "private-link.json")
    File.CreateSymbolicLink(link, target) |> ignore
    previewRefused context link
    let nested = Path.Combine(context.TemporaryDirectory, "private-nested")

    Directory.CreateDirectory(nested, ownerFile ||| UnixFileMode.UserExecute)
    |> ignore

    sourceIn nested "nested.json" (Encoding.UTF8.GetBytes("synthetic")) |> ignore
    let ancestor = Path.Combine(context.TemporaryDirectory, "private-ancestor-link")
    Directory.CreateSymbolicLink(ancestor, nested) |> ignore
    previewRefused context (Path.Combine(ancestor, "nested.json"))

let private preparation context =
    let operationId = Guid.NewGuid().ToString("D")
    let reference = "CC-PRIVATE-" + Guid.NewGuid().ToString("N")

    let values =
        [
            "incidentDate", "2026-08-01"
            "incidentNotificationDate", "2026-08-02"
            "incidentCountry", "Latvia"
            "claimantName", "Synthetic claimant"
            "insurerName", "Synthetic insurer"
            "claimedAmount", "10.00"
            "claimedCurrency", "EUR"
        ]

    use result =
        CliV3Fixtures.prepare operationId reference 0L "OPEN" values
        |> CliV3Fixtures.call context
        |> CliV3Fixtures.decode 0 "command.prepare"

    Expect.equal (CliV3Fixtures.outcomeKind result) "prepared" "Preparation is exportable"

    let digest =
        result.RootElement
            .GetProperty("outcome")
            .GetProperty("details")
            .GetProperty("summary")
            .GetProperty("requestSha256")
            .GetString()

    operationId,
    digest
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Missing digest")

let private exportRefused context operation digest path =
    let result =
        CliV3Fixtures.recoveryExport operation digest path |> CliV3Fixtures.call context

    noDisclosure result path
    use document = CliV3Fixtures.decode 3 "recovery.export" result

    Expect.equal
        (CliV3Fixtures.outcomeKind document)
        "localFailure"
        "Export refusal is an adapter outcome, not a core failure"

    let fault = document.RootElement.GetProperty("outcome").GetProperty("fault")
    let diagnostic = fault.GetProperty("diagnostic")
    Expect.equal (fault.GetProperty("code").GetString()) "STORE_UNAVAILABLE" "Local export code"

    Expect.equal
        (fault.GetProperty("recommendedAction").GetString())
        "STOP_AND_INVESTIGATE"
        "A file-write refusal does not authorize command replay"

    Expect.equal
        (diagnostic.GetProperty("id").GetString())
        "CLI_EXPORT_WRITE_FAILED"
        "Specific adapter cause"

    Expect.isTrue
        (diagnostic.GetProperty("parameters").EnumerateObject() |> Seq.isEmpty)
        "No private path or request content becomes a diagnostic argument"

let private exportBoundaries () =
    let context = DatabaseFixture.current ()
    let operation, digest = preparation context
    let destination = Path.Combine(context.TemporaryDirectory, "private-export.json")

    use exported =
        CliV3Fixtures.recoveryExport operation digest destination
        |> CliV3Fixtures.call context
        |> CliV3Fixtures.decode 0 "recovery.export"

    Expect.equal (CliV3Fixtures.outcomeKind exported) "exported" "Published export succeeds"
    Expect.equal (File.GetUnixFileMode(destination)) ownerFile "Published export is owner-only"
    let initialBytes = File.ReadAllBytes(destination)
    exportRefused context operation digest destination
    Expect.isTrue (File.ReadAllBytes(destination) = initialBytes) "Existing export is unchanged"
    let link = Path.Combine(context.TemporaryDirectory, "private-export-link.json")
    File.CreateSymbolicLink(link, destination) |> ignore
    exportRefused context operation digest link
    Expect.isTrue (File.ReadAllBytes(destination) = initialBytes) "Export link target is unchanged"
    let nested = Path.Combine(context.TemporaryDirectory, "private-export-nested")

    Directory.CreateDirectory(nested, ownerFile ||| UnixFileMode.UserExecute)
    |> ignore

    let ancestor =
        Path.Combine(context.TemporaryDirectory, "private-export-ancestor-link")

    Directory.CreateSymbolicLink(ancestor, nested) |> ignore
    let viaAncestor = Path.Combine(ancestor, "must-not-exist.json")
    exportRefused context operation digest viaAncestor
    Expect.isFalse (File.Exists(Path.Combine(nested, "must-not-exist.json"))) "No redirected export"

let tests =
    testList
        "published CLI private files"
        [
            testCase
                "[CC-CLI-002] published import refuses broad mode, invalid UTF-8, and oversize without disclosure"
                importModeUtf8AndSize
            testCase
                "[CC-CLI-002] published import refuses leaf and ancestor symlinks without disclosure"
                importLinks
            testCase
                "[CC-CLI-002] published export is owner-only, exclusive, and refuses links without disclosure"
                exportBoundaries
        ]
