module ClaimCore.Database.Program

open System
open System.Buffers
open System.Reflection
open System.Text.Json
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.Database

let private identity () =
    let assembly = Assembly.GetExecutingAssembly()

    let product =
        assembly.GetCustomAttribute<AssemblyProductAttribute>()
        |> Option.ofObj
        |> Option.map _.Product
        |> Option.defaultWith (fun () -> invalidOp "Compiled product identity is missing.")

    let version =
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        |> Option.ofObj
        |> Option.map _.InformationalVersion
        |> Option.defaultWith (fun () -> invalidOp "Compiled release identity is missing.")

    let assemblyVersion =
        assembly.GetName().Version
        |> Option.ofObj
        |> Option.map string
        |> Option.defaultWith (fun () -> invalidOp "Compiled assembly identity is missing.")

    product, version, assemblyVersion

let private recoveryHelp () =
    printfn "  ClaimCore.Database purge-live <private-proposal-file>"
    printfn "  ClaimCore.Database inspect-managed-copy <copy-id>"
    printfn "  ClaimCore.Database reconcile-lifecycle-event <event-id>"

    printfn
        "  ClaimCore.Database verify-restore-report <report> <signature> <evidence-index> <nonce>"

    printfn
        "  ClaimCore.Database verify-fenced-tail <report> <report-signature> <evidence-index> <fence> <fence-signature> <supplement> <supplement-signature> <nonce>"

    printfn "  ClaimCore.Database verify"
    printfn "  ClaimCore.Database verify-data"
    printfn "  ClaimCore.Database hold-backup-capture (private inherited control descriptor)"
    printfn "  ClaimCore.Database reconcile-backup-capture <lease-id>"

    printfn
        "  ClaimCore.Database issue-backup-health <private-policy-file> <private-independent-evidence-file> <private-certificate-output-file>"

    printfn
        "  ClaimCore.Database reconcile-backup-health <private-policy-file> <original-independent-evidence-file> <private-certificate-output-file>"

    printfn "  ClaimCore.Database prune [--dry-run] [--settled-retention-days <1-3650>]"
    printfn "                           [--abandoned-retention-days <1-3650>] [--limit <1-1000>]"
    printfn "  ClaimCore.Database describe diagnostics"
    printfn "  ClaimCore.Database help"
    printfn "  ClaimCore.Database version [--json]"
    printfn "Prune defaults: accepted 30 days; revoked 30 days; batch limit 100; deletion enabled."
    printfn "Set CLAIMCORE_ADMIN_CONNECTION_FILE to an owner-private schema-owner connection file."

    printfn
        "Witness commands also require separate owner-private witness connection and key-ring files."

    printfn "Initial ownership requires a private HTTPS issuer and immutable subject file."

let private help () =
    let _, version, _ = identity ()
    printfn "ClaimCore.Database %s — schema and recovery-retention administration" version
    printfn "  ClaimCore.Database initialize <canonical-IANA-ID>"
    printfn "  ClaimCore.Database initialize-real-data <canonical-IANA-ID>"

    printfn
        "  ClaimCore.Database publish-real-data-activation-plan <private-policy-file> <private-evidence-file> <private-review-output-file>"

    printfn
        "  ClaimCore.Database activate-real-data <private-policy-file> <original-evidence-file> <fresh-evidence-file> <plan-id> <first-approval-id> <second-approval-id>"

    printfn "  ClaimCore.Database reconcile-real-data-activation"

    printfn "  ClaimCore.Database initialize-witness"
    printfn "  ClaimCore.Database provision-initial-owner"

    printfn
        "  ClaimCore.Database register-copy-signer <purpose> <event-id> <key-id> <raw-public-key-file> <owner-approval-id> <holder-approval-id>"

    printfn
        "  ClaimCore.Database retire-copy-signer <purpose> <event-id> <key-id> <owner-approval-id> <holder-approval-id>"

    printfn "  ClaimCore.Database ingest-managed-copy <attestation-file> <signature-file>"
    printfn "  ClaimCore.Database transition-managed-copy <attestation-file> <signature-file>"
    printfn "  ClaimCore.Database verify-delete-managed-copy <attestation-file> <signature-file>"
    printfn "  ClaimCore.Database verify-managed-copy <attestation-file> <signature-file>"
    printfn "  ClaimCore.Database adopt-managed-copy <private-proposal-file>"
    printfn "  ClaimCore.Database publish-external-copy <private-proposal-file>"
    printfn "  ClaimCore.Database transition-adopted-copy <canonical-file> <signature-file>"
    printfn "  ClaimCore.Database verify-delete-adopted-copy <canonical-file> <signature-file>"
    printfn "  ClaimCore.Database certify-managed-payload-absence <private-proposal-file>"
    printfn "  ClaimCore.Database complete-suppression-horizon <private-proposal-file>"
    printfn "  ClaimCore.Database prepare-writer-handoff <canonical-file> <signature-file>"
    printfn "  ClaimCore.Database settle-writer-handoff <canonical-file> <signature-file>"

    printfn
        "  ClaimCore.Database activate-writer-handoff <report> <report-signature> <evidence-index> <fence> <fence-signature> <supplement> <supplement-signature>"

    printfn
        "  ClaimCore.Database abort-writer-handoff <candidate-file> <owner-one-signature-file> <owner-two-signature-file>"

    printfn
        "  ClaimCore.Database draft-writer-handoff-abort <handoff-id> <abort-key-one-id> <abort-key-two-id> <new-private-candidate-file>"

    recoveryHelp ()

let private writeVersion () =
    let product, version, assemblyVersion = identity ()
    let buffer = ArrayBufferWriter<byte>()
    use writer = new Utf8JsonWriter(buffer)
    writer.WriteStartObject()
    writer.WriteString("application", product)
    writer.WriteString("version", version)
    writer.WriteString("assemblyVersion", assemblyVersion)
    writer.WriteEndObject()
    writer.Flush()
    let output = Console.OpenStandardOutput()
    output.Write(buffer.WrittenSpan)
    output.WriteByte(byte '\n')
    output.Flush()
    0

let private dispatch argv =
    let output = Console.OpenStandardOutput()
    let errors = Console.OpenStandardError()

    match DatabaseArguments.parse (Array.toList argv) with
    | Error reason -> DatabaseExecution.inputFailure reason errors 64
    | Ok DatabaseCommand.Help ->
        help ()
        Console.Out.Flush()
        0
    | Ok DatabaseCommand.Version ->
        let _, version, _ = identity ()
        Console.Out.WriteLine version
        Console.Out.Flush()
        0
    | Ok DatabaseCommand.VersionJson -> writeVersion ()
    | Ok DatabaseCommand.Diagnostics ->
        output.Write(DatabaseContracts.catalogue ())
        output.Flush()
        0
    | Ok command -> DatabaseExecution.run command output errors

[<EntryPoint>]
let main argv =
    try
        dispatch argv
    with _ ->
        DatabaseExecution.inputFailure
            DatabaseInputProblem.ProcessFailed
            (Console.OpenStandardError())
            70
