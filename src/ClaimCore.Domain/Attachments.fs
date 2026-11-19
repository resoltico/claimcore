namespace ClaimCore.Domain

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text

module AttachmentLimits =
    [<Literal>]
    let FileBytes = 26_214_400

    [<Literal>]
    let RetainedFiles = 100L

    [<Literal>]
    let RetainedBytes = 268_435_456L

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type AttachmentId = private AttachmentId of Guid

module AttachmentId =
    let create value =
        if value = Guid.Empty then
            None
        else
            Some(AttachmentId value)

    let ofOperationId operation =
        AttachmentId(OperationId.value operation)

    let value (AttachmentId value) = value
    let format id = (value id).ToString("D")

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type AttachmentName = private AttachmentName of string

module AttachmentName =
    [<Literal>]
    let MaximumBytes = 120

    [<Literal>]
    let MaximumCandidateBytes = 4096

    let private utf8 = UTF8Encoding(false, true)
    let private fallback = "attachment"

    let private reserved =
        set
            [
                "CON"
                "PRN"
                "AUX"
                "NUL"
                for digit in "123456789" do
                    $"COM{digit}"
                    $"LPT{digit}"
            ]

    let private meaningful (text: string) =
        text.EnumerateRunes()
        |> Seq.exists (fun rune -> Rune.IsLetter rune || Rune.IsDigit rune)

    let private deviceName (text: string) =
        let first = (text.Split('.', 2)[0]).ToUpperInvariant()
        reserved.Contains(first.Normalize(NormalizationForm.FormKC))

    let private safeStem (text: string) =
        let stem = text.Trim('.')

        if not (meaningful stem) then fallback
        elif stem.StartsWith('-') || deviceName stem then "_" + stem
        else stem

    let private truncate limit (text: string) =
        let elements = StringInfo.GetTextElementEnumerator text
        let mutable used = 0
        let mutable stop = false
        let result = StringBuilder()

        while not stop && elements.MoveNext() do
            let element = elements.GetTextElement()
            let size = utf8.GetByteCount element

            if size > limit - used then
                stop <- true
            else
                result.Append element |> ignore
                used <- used + size

        result.ToString()

    let private fit (cleaned: string) =
        let separator = cleaned.LastIndexOf '.'

        let stem, extension =
            if separator < 0 then
                cleaned, ""
            else
                let suffix = cleaned.Substring(separator + 1)

                if utf8.GetByteCount suffix <= 16 && meaningful suffix then
                    cleaned.Substring(0, separator), "." + suffix
                else
                    cleaned, ""

        // Reserve room for a prefix if truncation itself exposes a device name.
        let budget = MaximumBytes - utf8.GetByteCount extension
        let shortened = truncate budget (safeStem stem)
        let checkedStem = safeStem shortened

        if utf8.GetByteCount checkedStem <= budget then
            checkedStem + extension
        else
            safeStem (truncate (budget - 1) shortened) + extension

    let private clean (candidate: string) =
        let basename = candidate.Substring(candidate.LastIndexOfAny([| '/'; '\\' |]) + 1)
        let result = StringBuilder()
        let mutable whitespace = false

        for rune in basename.Normalize(NormalizationForm.FormC).EnumerateRunes() do
            let category = Rune.GetUnicodeCategory rune

            if category = UnicodeCategory.Control || category = UnicodeCategory.Format then
                ()
            elif Rune.IsWhiteSpace rune then
                if not whitespace then
                    result.Append '_' |> ignore

                whitespace <- true
            else
                whitespace <- false

                if
                    Rune.IsLetter rune
                    || Rune.IsDigit rune
                    || category = UnicodeCategory.NonSpacingMark
                    || category = UnicodeCategory.SpacingCombiningMark
                    || category = UnicodeCategory.EnclosingMark
                    || rune.Value = int '.'
                    || rune.Value = int '_'
                    || rune.Value = int '-'
                then
                    result.Append(rune.ToString()) |> ignore

        let cleanBasename = result.ToString().Normalize(NormalizationForm.FormC).Trim('.')

        let cleaned =
            if deviceName (basename.Trim('.')) && not (deviceName cleanBasename) then
                "_" + cleanBasename
            else
                cleanBasename

        let bounded = fit cleaned

        // Shortening a long suffix can turn it into a usable extension.
        // Normalize the resulting bounded component as well for stable readmission.
        fit bounded

    let parse (input: string | null) =
        match input with
        | null -> None
        | input ->
            try
                if utf8.GetByteCount input > MaximumCandidateBytes then
                    None
                else
                    Some(AttachmentName(clean input))
            with :? EncoderFallbackException ->
                None

    let value (AttachmentName value) = value

    let internal suffix (AttachmentName value) =
        match value.LastIndexOf '.' with
        | -1 -> None
        | index -> Some(value.Substring(index + 1))

module AttachmentType =
    // Microsoft Outlook's Level 1 list, retrieved 2026-11-19, plus platform launchers and installers.
    // https://support.microsoft.com/en-us/outlook/blocked-attachments-in-outlook
    let private blocked =
        HashSet<string>(
            [
                "ade"
                "adp"
                "apk"
                "app"
                "appcontent-ms"
                "application"
                "appref-ms"
                "appx"
                "asp"
                "aspx"
                "asx"
                "bas"
                "bat"
                "bgi"
                "cab"
                "cdxml"
                "cer"
                "chm"
                "cmd"
                "cnt"
                "com"
                "cpl"
                "crt"
                "csh"
                "der"
                "diagcab"
                "exe"
                "fxp"
                "gadget"
                "grp"
                "hlp"
                "hpj"
                "hta"
                "htc"
                "img"
                "inf"
                "ins"
                "iso"
                "isp"
                "its"
                "jar"
                "jnlp"
                "js"
                "jse"
                "ksh"
                "library-ms"
                "lnk"
                "mad"
                "maf"
                "mag"
                "mam"
                "maq"
                "mar"
                "mas"
                "mat"
                "mau"
                "mav"
                "maw"
                "mcf"
                "mda"
                "mdb"
                "mde"
                "mdt"
                "mdw"
                "mdz"
                "msc"
                "mht"
                "mhtml"
                "msh"
                "msh1"
                "msh2"
                "mshxml"
                "msh1xml"
                "msh2xml"
                "msi"
                "msp"
                "mst"
                "msu"
                "ops"
                "osd"
                "pcd"
                "pif"
                "pl"
                "plg"
                "prf"
                "prg"
                "printerexport"
                "ps1"
                "ps1xml"
                "ps2"
                "ps2xml"
                "psc1"
                "psc2"
                "psd1"
                "psdm1"
                "pssc"
                "pst"
                "py"
                "pyc"
                "pyo"
                "pyw"
                "pyz"
                "pyzw"
                "reg"
                "scf"
                "scr"
                "sct"
                "search-ms"
                "settingcontent-ms"
                "shb"
                "shs"
                "theme"
                "tmp"
                "udl"
                "url"
                "vb"
                "vbe"
                "vbp"
                "vbs"
                "vhd"
                "vhdx"
                "vsmacros"
                "vsw"
                "webpnp"
                "website"
                "ws"
                "wsb"
                "wsc"
                "wsf"
                "wsh"
                "xbap"
                "xll"
                "xnk"
                "msix"
                "msixbundle"
                "appxbundle"
                "appinstaller"
                "sh"
                "bash"
                "zsh"
                "command"
                "tool"
                "terminal"
                "desktop"
                "appimage"
                "run"
                "deb"
                "rpm"
                "pkg"
                "mpkg"
                "dmg"
            ],
            StringComparer.OrdinalIgnoreCase
        )

    let refused name =
        AttachmentName.suffix name |> Option.exists blocked.Contains

[<Struct; System.Runtime.CompilerServices.IsReadOnly>]
type AttachmentDigest = private AttachmentDigest of string

module AttachmentDigest =
    let parse (input: string | null) =
        match input with
        | null -> None
        | input when input.Length = 64 && Seq.forall Char.IsAsciiHexDigit input ->
            Some(AttachmentDigest(input.ToLowerInvariant()))
        | _ -> None

    let value (AttachmentDigest digest) = digest

    let hash (bytes: ReadOnlySpan<byte>) =
        AttachmentDigest(Convert.ToHexStringLower(SHA256.HashData bytes))

type AttachmentIntent =
    private
        {
            Name: AttachmentName
            Length: int64
            Digest: AttachmentDigest
        }

module AttachmentIntent =
    let create name length digest =
        if length < 0L || length > int64 AttachmentLimits.FileBytes then
            None
        else
            Some
                {
                    Name = name
                    Length = length
                    Digest = digest
                }

    let name intent = intent.Name
    let length intent = intent.Length
    let digest intent = intent.Digest

type VerifiedAttachment = private VerifiedAttachment of AttachmentIntent * byte array

module VerifiedAttachment =
    let verify intent (bytes: ReadOnlyMemory<byte>) =
        if int64 bytes.Length <> AttachmentIntent.length intent then
            None
        else
            let snapshot = bytes.ToArray()

            if
                AttachmentDigest.hash (ReadOnlySpan<byte> snapshot) = AttachmentIntent.digest intent
            then
                Some(VerifiedAttachment(intent, snapshot))
            else
                None

    let intent (VerifiedAttachment(intent, _)) = intent

    let bytes (VerifiedAttachment(_, bytes)) = ReadOnlySpan<byte> bytes

    let memory (VerifiedAttachment(_, bytes)) = ReadOnlyMemory<byte> bytes

/// The two disclosure fields shared by current lists and permitted history views.
type AttachmentDisplay = { Name: AttachmentName; Length: int64 }

type AttachmentDescriptor =
    {
        Blob: AttachmentId
        Intent: AttachmentIntent
    }

module AttachmentDescriptor =
    let display (descriptor: AttachmentDescriptor) =
        {
            AttachmentDisplay.Name = AttachmentIntent.name descriptor.Intent
            Length = AttachmentIntent.length descriptor.Intent
        }

type RetainedAttachmentMatch =
    private
        {
            Blob: AttachmentId
            Current: AttachmentId option
        }

module RetainedAttachmentMatch =
    /// A lookup by digest has selected a candidate; exact equality is required to reuse it.
    let verify
        wanted
        origin
        (descriptor: AttachmentDescriptor)
        current
        (stored: ReadOnlySpan<byte>)
        =
        let intent = VerifiedAttachment.intent wanted

        if
            descriptor.Blob = origin
            && AttachmentIntent.length descriptor.Intent = AttachmentIntent.length intent
            && AttachmentIntent.digest descriptor.Intent = AttachmentIntent.digest intent
            && (VerifiedAttachment.bytes wanted).SequenceEqual stored
        then
            Some { Blob = origin; Current = current }
        else
            None

[<RequireQualifiedAccess>]
type AttachmentAdmission =
    | RetainNew
    | Reattach of blob: AttachmentId
    | Duplicate of current: AttachmentId
    | RetainedCapacityReached

module AttachmentAdmission =
    /// Bytes from erased descriptors are unknown. Check the known lower bound in that case.
    let canRetainNew count knownBytes length =
        count < AttachmentLimits.RetainedFiles
        && knownBytes <= AttachmentLimits.RetainedBytes - (Option.defaultValue 0L length)

    /// The caller supplies a byte-verified, case-scoped match and locked retained usage.
    let decide attachment count bytes matched =
        let intent = VerifiedAttachment.intent attachment

        if count < 0L || bytes < 0L then
            invalidArg (nameof count) "Retained attachment usage must be nonnegative."

        match matched with
        | Some { Current = Some current } -> AttachmentAdmission.Duplicate current
        | Some { Blob = blob } -> AttachmentAdmission.Reattach blob
        | None when not (canRetainNew count bytes (Some(AttachmentIntent.length intent))) ->
            AttachmentAdmission.RetainedCapacityReached
        | None -> AttachmentAdmission.RetainNew

/// Retained addition history identifies which effect first established the payload.
[<RequireQualifiedAccess>]
type AttachmentEffect =
    | Added of introducesBlob: bool * descriptor: AttachmentDescriptor option
    | Withdrawn

module AttachmentEffect =
    let clear =
        function
        | AttachmentEffect.Added(introduces, _) -> AttachmentEffect.Added(introduces, None)
        | AttachmentEffect.Withdrawn -> AttachmentEffect.Withdrawn

    let canFollow attachmentId operation previous =
        function
        | AttachmentEffect.Added(introduces, descriptor) ->
            previous = None
            && attachmentId = AttachmentId.ofOperationId operation
            && (descriptor
                |> Option.forall (fun descriptor -> introduces = (descriptor.Blob = attachmentId)))
        | AttachmentEffect.Withdrawn ->
            match previous with
            | Some(AttachmentEffect.Added _) -> true
            | _ -> false

    /// Old reads consult the exact ID's latest accepted state in the authorized snapshot.
    let disclose latest historical =
        match latest, historical with
        | Some(AttachmentEffect.Added(_, Some _)), AttachmentEffect.Added(_, Some descriptor) ->
            Some(AttachmentDescriptor.display descriptor)
        | _ -> None
