module ClaimCore.Tests.AttachmentPolicyTests

open System
open System.Text
open Expecto
open ClaimCore.Domain

let private name candidate =
    AttachmentName.parse candidate |> Option.get

let private digest =
    AttachmentDigest.parse "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    |> Option.get

let private intent length =
    AttachmentIntent.create (name "Evidence") length digest

let private id (value: string) =
    AttachmentId.create (Guid.Parse value) |> Option.get

let private classify bytes =
    AttachmentPreviewProfile.classify
        AttachmentPreviewCandidate.Raster
        (ReadOnlySpan<byte>(bytes: byte array))

// A single red RGBA pixel with valid chunk CRCs. The negative vectors change a single
// eligibility property and carry their own corresponding chunk CRCs.
let private png =
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="

[<Tests>]
let tests =
    testList
        "attachment core policy"
        [
            testCase "portable names retain stable Unicode spelling" (fun () ->
                for candidate, expected in
                    [
                        "Arrival Photo 01.jpg", "Arrival_Photo_01.jpg"
                        "dir/sub\\Arrival\u2003\u00A0Photo.jpg", "Arrival_Photo.jpg"
                        "Re\u0301sume\u0301.txt", "Résumé.txt"
                        "safe\r\n\u202Ename.txt", "safename.txt"
                        "...", "attachment"
                        "-report.txt...", "_-report.txt"
                        "Con.tar.gz", "_Con.tar.gz"
                        "COM¹.jpg", "_COM.jpg"
                        "lPt².png", "_lPt.png"
                    ] do
                    let canonical = name candidate
                    Expect.equal (AttachmentName.value canonical) expected candidate

                    Expect.equal
                        (name expected)
                        canonical
                        "The canonical basename re-admits unchanged")

            testCase "shortening preserves complete graphemes and usable extension" (fun () ->
                let stem = String.replicate 39 "a\u0301\u0304"
                let canonical = name (stem + ".jpg") |> AttachmentName.value
                Expect.equal canonical (String.replicate 29 "á\u0304" + ".jpg") "Whole graphemes"
                Expect.equal (Encoding.UTF8.GetByteCount canonical) 120 "Inclusive byte limit"

                Expect.equal
                    (name canonical |> AttachmentName.value)
                    canonical
                    "Stable under readmission"

                let prefix = String.replicate 108 "x"
                let shortened = name (prefix + "....abcdefghijklmnopq")

                Expect.equal
                    (AttachmentName.value shortened)
                    (prefix + ".abcdefgh")
                    "Shortened suffix"

                Expect.equal
                    (name (AttachmentName.value shortened))
                    shortened
                    "Stable shortened suffix"

                Expect.isNone
                    (AttachmentName.parse (String.replicate 4097 "x"))
                    "Bound input before work")

            testCase "intake refuses launchable suffixes of the final canonical name" (fun () ->
                for candidate in
                    [
                        "report.exe"
                        "REPORT.EXE"
                        "a.b.c.ps1"
                        "x.settingcontent-ms"
                        "photo.jpg.lnk"
                        String.replicate 140 "x" + ".exe"
                    ] do
                    Expect.isTrue (name candidate |> AttachmentType.refused) candidate

                for candidate in
                    [
                        "evidence.zip"
                        "letter.docx"
                        "signed.asice"
                        "signed.bdoc"
                        "signed.edoc"
                        "mail.eml"
                        "scan.pdf"
                        "README"
                        "exe.notes.txt"
                        "evidence.jśs"
                        String.replicate 140 "x" + ".settingcontent-ms"
                    ] do
                    Expect.isFalse (name candidate |> AttachmentType.refused) candidate

                for candidate, canonical, refused in
                    [
                        "evidence.exe.", "evidence.exe", true
                        "evidence.exe ", "evidence.exe_", false
                        "evidence.txt:x.exe", "evidence.txtx.exe", true
                    ] do
                    let admitted = name candidate
                    Expect.equal (AttachmentName.value admitted) canonical candidate
                    Expect.equal (AttachmentType.refused admitted) refused canonical)

            testCase "verified bytes include empty and reject descriptor mismatch" (fun () ->
                let empty = intent 0L |> Option.get
                let verified = VerifiedAttachment.verify empty ReadOnlyMemory<byte>.Empty
                Expect.isSome verified "Empty bytes prove their presence by their digest"

                Expect.equal
                    (VerifiedAttachment.bytes (Option.get verified)).Length
                    0
                    "Exact empty bytes"

                Expect.isNone
                    (VerifiedAttachment.verify empty (ReadOnlyMemory [| 0uy |]))
                    "A different count fails"

                let oneByte = intent 1L |> Option.get

                Expect.isNone
                    (VerifiedAttachment.verify oneByte (ReadOnlyMemory [| 0uy |]))
                    "A matching count with a different digest fails")

            testCase
                "verification retains the admitted bytes independently of the source buffer"
                (fun () ->
                    let source = [| 97uy; 98uy; 99uy |]

                    let expected =
                        AttachmentDigest.parse
                            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                        |> Option.get

                    let selected =
                        AttachmentIntent.create (name "example.bin") 3L expected |> Option.get

                    let admitted =
                        VerifiedAttachment.verify selected (ReadOnlyMemory source) |> Option.get

                    source[0] <- 0uy

                    Expect.sequenceEqual
                        ((VerifiedAttachment.bytes admitted).ToArray())
                        [ 97uy; 98uy; 99uy ]
                        "Mutation of the caller's source cannot alter verified bytes")

            testCase
                "retained quotas are inclusive and already retained blobs reuse them"
                (fun () ->
                    Expect.isSome (intent 26214400L) "Exact per-file maximum"
                    Expect.isNone (intent 26214401L) "One byte over the per-file limit"
                    Expect.isNone (intent -1L) "Negative lengths cannot be admitted"

                    let prepare (bytes: byte array) sha =
                        let digest = AttachmentDigest.parse sha |> Option.get

                        let intent =
                            AttachmentIntent.create (name "Example") (int64 bytes.Length) digest
                            |> Option.get

                        VerifiedAttachment.verify intent (ReadOnlyMemory bytes) |> Option.get

                    let empty =
                        VerifiedAttachment.verify
                            (intent 0L |> Option.get)
                            ReadOnlyMemory<byte>.Empty
                        |> Option.get

                    let one =
                        prepare
                            [| 97uy |]
                            "ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb"

                    let two =
                        prepare
                            [| 97uy; 98uy |]
                            "fb8e20fc2e4c3f248c60c39bd652f3c1347298bb977b8b4d5903b85055620603"

                    let select = AttachmentAdmission.decide

                    Expect.equal
                        (select one 99L 268435455L None)
                        AttachmentAdmission.RetainNew
                        "Both exact caps"

                    Expect.equal
                        (select two 99L 268435455L None)
                        AttachmentAdmission.RetainedCapacityReached
                        "One byte over retained capacity"

                    Expect.equal
                        (select empty 100L 10L None)
                        AttachmentAdmission.RetainedCapacityReached
                        "A new empty blob still needs a file slot"

                    let blob = id "51a5e535-1785-4d5e-a41d-0bf811d5d670"
                    let current = id "49e3f4ed-c639-4c9b-b8c5-5d676f09c103"

                    let descriptor =
                        {
                            Blob = blob
                            Intent = VerifiedAttachment.intent one
                            Preview = None
                        }

                    let matchBytes active =
                        RetainedAttachmentMatch.verify
                            one
                            blob
                            descriptor
                            active
                            (ReadOnlySpan<byte> [| 97uy |])

                    let withdrawn = matchBytes None
                    let active = matchBytes (Some current)

                    Expect.isSome withdrawn "Exact bytes and accepting origin match"

                    Expect.isNone
                        (RetainedAttachmentMatch.verify
                            one
                            blob
                            descriptor
                            None
                            (ReadOnlySpan<byte> [| 98uy |]))
                        "A matching descriptor cannot substitute different bytes"

                    Expect.isNone
                        (RetainedAttachmentMatch.verify
                            one
                            current
                            descriptor
                            None
                            (ReadOnlySpan<byte> [| 97uy |]))
                        "The retained origin must also agree"

                    Expect.isNone
                        (RetainedAttachmentMatch.verify
                            one
                            blob
                            { descriptor with
                                Intent = VerifiedAttachment.intent two
                                Preview = None
                            }
                            None
                            (ReadOnlySpan<byte> [| 97uy |]))
                        "The committed descriptor must agree"

                    Expect.equal
                        (select one 100L 268435456L withdrawn)
                        (AttachmentAdmission.Reattach blob)
                        "Retained bytes reuse quota"

                    Expect.equal
                        (select one 100L 268435456L active)
                        (AttachmentAdmission.Duplicate current)
                        "The current ID identifies an active exact match")

            testCase
                "scoped readers can read, with changes limited to active cases and human handlers"
                (fun () ->
                    let openCase = TaskBuilders.family ()

                    let caseAt lifecycle status =
                        TaskBuilders.setCase lifecycle status (TaskBuilders.family ())

                    let snapshot family =
                        ClaimCore.Domain.Aggregates.CaseFamily.snapshot family |> Option.get

                    let stamp = TaskBuilders.context 2
                    let scope = Scope.Case openCase.Id

                    let scoped principal role =
                        AccessView.ofActor (
                            Some
                                {
                                    Actor =
                                        { TaskBuilders.actor with
                                            Principal = principal
                                        }
                                    Grants = [ { Role = role; Scope = scope } ]
                                }
                        )

                    let human = stamp.Decision.Principal
                    let handler = scoped human Role.CaseHandler
                    let reader = scoped human Role.CaseReader

                    let service =
                        PrincipalKey.service "https://issuer.example/" "attachment-client"
                        |> TaskBuilders.accepted

                    let serviceReader = scoped service Role.CaseReader
                    let closed = caseAt Lifecycle.Active CaseStatus.Closed
                    let voided = caseAt Lifecycle.Voided CaseStatus.Opened
                    let erased = caseAt Lifecycle.Erased CaseStatus.Opened

                    let wrongParent =
                        { snapshot openCase with
                            CaseId = CaseId.ofOperationId stamp.Stamp.OperationId
                        }

                    let unavailable = Refusal.Access AccessRefusal.ResourceUnavailable

                    for principal, view in
                        [ human, handler; human, reader; service, serviceReader ] do
                        for family in [ openCase; closed; voided ] do
                            Expect.isOk
                                (AttachmentAccess.read principal view (snapshot family))
                                "Readable case"

                        for snapshot in [ snapshot erased; wrongParent ] do
                            Expect.equal
                                (AttachmentAccess.read principal view snapshot)
                                (Error unavailable)
                                "Unavailable case"

                    let upload =
                        match ClaimCore.Domain.Tasks.AttachmentTasks.add with
                        | ClaimCore.Domain.Tasks.Task.Family task -> task
                        | _ -> failtest "Attachment addition belongs to the case family."

                    let inputs =
                        TaskBuilders.inputs
                            [
                                TaskBuilders.text InputTarget.AttachmentName "manifest.txt"
                                TaskBuilders.text InputTarget.AttachmentLength "0"
                                TaskBuilders.text
                                    InputTarget.AttachmentDigest
                                    (AttachmentDigest.value digest)
                            ]

                    let decide
                        principal
                        access
                        maintenance
                        (family: ClaimCore.Domain.Aggregates.CaseFamily)
                        =
                        let context =
                            { stamp with
                                Decision =
                                    { stamp.Decision with
                                        Principal = principal
                                        Access = access
                                        Maintenance = maintenance
                                    }
                            }

                        upload.Decide
                            context
                            { TaskBuilders.environment with
                                Requester = access
                                RequesterPrincipal = principal
                                Maintenance = maintenance
                            }
                            (Some(ClaimCore.Domain.Aggregates.DecisionFacts.fromReplay family))
                            (Binding.AttachmentNew(CaseSelector.Id family.Id))
                            inputs

                    for family in [ openCase; closed ] do
                        Expect.isOk (decide human handler false family) "Human handler"

                    decide human handler false voided
                    |> TaskBuilders.expectRefusal (
                        Refusal.Case(CaseRefusal.NotOffered CaseHeadline.Voided)
                    )

                    decide human handler true openCase
                    |> TaskBuilders.expectRefusal Refusal.RegisterMaintenance

                    for principal, view in [ human, reader; service, serviceReader ] do
                        decide principal view false openCase
                        |> TaskBuilders.expectRefusal (
                            Refusal.Access(AccessRefusal.NotAuthorized Capability.ManageCaseWork)
                        ))

            testCase "withdrawal keeps structural evidence and closes old disclosure" (fun () ->
                let firstOperation = TaskBuilders.operation 2
                let nextOperation = TaskBuilders.operation 3

                let first = AttachmentId.ofOperationId firstOperation
                let next = AttachmentId.ofOperationId nextOperation

                let descriptor =
                    {
                        Blob = first
                        Intent = intent 0L |> Option.get
                        Preview = None
                    }

                let added = AttachmentEffect.Added(true, Some descriptor)
                let withdrawn = AttachmentEffect.Withdrawn
                let reattached = AttachmentEffect.Added(false, Some descriptor)

                Expect.isTrue
                    (AttachmentEffect.canFollow first firstOperation None added)
                    "Accept the first blob origin"

                Expect.isFalse
                    (AttachmentEffect.canFollow next nextOperation None added)
                    "Reject a claimed origin for another ID"

                Expect.isTrue
                    (AttachmentEffect.canFollow first nextOperation (Some added) withdrawn)
                    "Record withdrawal"

                Expect.isFalse
                    (AttachmentEffect.canFollow first nextOperation (Some withdrawn) withdrawn)
                    "Twice is invalid"

                Expect.isFalse
                    (AttachmentEffect.canFollow first firstOperation (Some withdrawn) added)
                    "The withdrawn ID is terminal"

                Expect.isTrue
                    (AttachmentEffect.canFollow next nextOperation None reattached)
                    "Reuse the original blob at a new ID"

                Expect.equal
                    (AttachmentEffect.disclose (Some added) added)
                    (Some
                        {
                            AttachmentDisplay.Name = name "Evidence"
                            Length = 0L
                        })
                    "The disclosed view contains the name and size"

                Expect.isNone
                    (AttachmentEffect.disclose (Some withdrawn) added)
                    "The old addition is withheld"

                Expect.isNone
                    (AttachmentEffect.disclose (Some withdrawn) withdrawn)
                    "Withdrawal carries no descriptor"

                Expect.equal
                    (AttachmentEffect.clear added)
                    (AttachmentEffect.Added(true, None))
                    "Erasure retains the origin marker"

                Expect.isNone
                    (AttachmentEffect.disclose (Some(AttachmentEffect.clear added)) added)
                    "Erasure denies disclosure")
            testCase "static PNG profile refuses animated, expanded and ambiguous inputs" (fun () ->
                let original = Convert.FromBase64String png
                Expect.equal (classify original) (Some AttachmentPreview.Png) "Static PNG"

                for encoded in
                    [
                        // Side 8,193 pixels and area 8,193 pixels.
                        "iVBORw0KGgoAAAANSUhEUgAAIAEAAAABCAYAAACZiUteAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="
                        // Both sides are allowed individually, with an area of 16,004,000 pixels.
                        "iVBORw0KGgoAAAANSUhEUgAAD6AAAA+hCAYAAAAlIzCnAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="
                        // Same image with an acTL chunk.
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACGFjVEwAAAABAAAAALQt6aAAAAANSURBVHicY/jPwPAfAAUAAf+JmT0dAAAAAElFTkSuQmCC"
                        // Same image with independent compressed text.
                        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAEnpUWHRDb21tZW50AAB4nKsAAAB5AHkU0u6/AAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="
                    ] do
                    Expect.isNone
                        (classify (Convert.FromBase64String encoded))
                        "Excluded PNG structure"

                Expect.isNone (classify original[..7]) "Signature alone is insufficient"
                Expect.isNone (classify original[.. original.Length - 2]) "Complete IEND required"
                Expect.isNone (classify (Array.append original [| 0uy |])) "Trailing bytes refused"
                original[29] <- original[29] ^^^ 1uy
                Expect.isNone (classify original) "A bad CRC refuses preview")

            testCase "JPEG profile requires a complete supported frame and scan" (fun () ->
                let shape marker height width =
                    // A framing probe rather than a decodable image: SOI, SOF, SOS, entropy, EOI.
                    [|
                        0xFFuy
                        0xD8uy
                        0xFFuy
                        marker
                        0uy
                        11uy
                        8uy
                        byte (height >>> 8)
                        byte height
                        byte (width >>> 8)
                        byte width
                        1uy
                        1uy
                        0x11uy
                        0uy
                        0xFFuy
                        0xDAuy
                        0uy
                        8uy
                        1uy
                        1uy
                        0uy
                        0uy
                        63uy
                        0uy
                        1uy
                        0xFFuy
                        0x00uy
                        0xFFuy
                        0xD9uy
                    |]

                for frame in [ 0xC0uy; 0xC2uy ] do
                    Expect.equal
                        (classify (shape frame 1 1))
                        (Some AttachmentPreview.Jpeg)
                        "Baseline and progressive frame shapes"

                for frame, height, width in
                    [ 0xC1uy, 1, 1; 0xC0uy, 0, 1; 0xC0uy, 1, 8193; 0xC0uy, 4000, 4001 ] do
                    Expect.isNone (classify (shape frame height width)) "Excluded JPEG frame"

                let original = shape 0xC0uy 1 1
                Expect.isNone (classify original[.. original.Length - 2]) "EOI is required"
                original[5] <- 255uy
                Expect.isNone (classify original) "Segments must fit in the file")
        ]
