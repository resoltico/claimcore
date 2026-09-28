namespace ClaimCore.Database

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Postgres

/// A publish manifest is independently signed and pins the exact shipped Database binary.
/// A checkout or owner-selected configuration file cannot create this trust anchor.
[<NoEquality; NoComparison>]
type internal TrustedRestorePublication =
    {
        ManifestSha256: string
        VerifierBinarySha256: string
        ReportSignerKeyId: Guid
        CheckpointSignerKeyId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        WriterGeneration: int64
        WitnessCutoff: int64
        WitnessCutoffHash: string
    }

module internal DatabaseRestorePublication =
    let reviewedRootKey () =
        ReviewedDeploymentRoot.publicationKey ()

    let private fields =
        [
            "format"
            "publicationId"
            "verifierBinarySha256"
            "reportSignerKeyId"
            "checkpointSignerKeyId"
            "installationId"
            "lineageId"
            "epoch"
            "writerGeneration"
            "witnessCutoff"
            "witnessCutoffHash"
            "issuedAt"
            "validUntil"
        ]

    let private sha (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               ('0' <= character && character <= '9') || ('a' <= character && character <= 'f'))

    let private text (root: JsonElement) name =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Publication identity is invalid.")

    let private uuid root name =
        let encoded = text root name
        let value = Guid.ParseExact(encoded, "D")

        if value = Guid.Empty || encoded <> value.ToString("D") then
            invalidOp "Publication identity is invalid."

        value

    let private instant root name =
        DateTimeOffset.ParseExact(
            text root name,
            "yyyy-MM-ddTHH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
        )

    let private validClaims
        (value: JsonElement)
        binarySha256
        (now: DateTimeOffset)
        issued
        expires
        binary
        hash
        reportKey
        checkpointKey
        generation
        epoch
        cutoff
        =
        DatabaseRestoreCanonical.exactProperties fields value
        && text value "format" = "claimcore-publication-manifest-1"
        && uuid value "publicationId" <> Guid.Empty
        && binary = binarySha256
        && sha binary
        && sha hash
        && reportKey <> checkpointKey
        && generation >= 1L
        && epoch >= 1L
        && cutoff >= 0L
        && issued <= now
        && now < expires
        && expires <= issued.AddDays(7.)

    let private claims (value: JsonElement) (manifest: byte array) binarySha256 now =
        let issued = instant value "issuedAt"
        let expires = instant value "validUntil"
        let binary = text value "verifierBinarySha256"
        let hash = text value "witnessCutoffHash"
        let reportKey = uuid value "reportSignerKeyId"
        let checkpointKey = uuid value "checkpointSignerKeyId"
        let generation = DatabaseRestoreCanonical.number "writerGeneration" value
        let epoch = DatabaseRestoreCanonical.number "epoch" value
        let cutoff = DatabaseRestoreCanonical.number "witnessCutoff" value

        if
            not (
                validClaims
                    value
                    binarySha256
                    now
                    issued
                    expires
                    binary
                    hash
                    reportKey
                    checkpointKey
                    generation
                    epoch
                    cutoff
            )
        then
            None
        else
            Some
                {
                    ManifestSha256 = SHA256.HashData(manifest) |> Convert.ToHexStringLower
                    VerifierBinarySha256 = binary
                    ReportSignerKeyId = reportKey
                    CheckpointSignerKeyId = checkpointKey
                    InstallationId = uuid value "installationId"
                    LineageId = uuid value "lineageId"
                    Epoch = epoch
                    WriterGeneration = generation
                    WitnessCutoff = cutoff
                    WitnessCutoffHash = hash
                }

    let verifyWithRoot
        (rootKey: byte array)
        (manifest: byte array)
        (signature: byte array)
        binarySha256
        (now: DateTimeOffset)
        : TrustedRestorePublication option =
        try
            if
                rootKey.Length <> 32
                || signature.Length <> 64
                || manifest.Length < 2
                || manifest.Length > 16384
                || not (ManagedCopySignature.verify rootKey manifest signature)
            then
                None
            else
                match DatabaseRestoreCanonical.parse manifest with
                | None -> None
                | Some document ->
                    use document = document
                    claims document.RootElement manifest binarySha256 now
        with _ ->
            None

    let private fixedFiles () =
        let assembly = typeof<TrustedRestorePublication>.Assembly.Location

        if String.IsNullOrWhiteSpace assembly then
            invalidOp "Published Database assembly is unavailable."

        let directory =
            Path.GetDirectoryName assembly
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                invalidOp "Published Database directory is unavailable.")

        Path.Combine(directory, "claimcore-publication.json"),
        Path.Combine(directory, "claimcore-publication.sig")

    let private readBounded limit path =
        let file = FileInfo(path)

        if not file.Exists || file.Length < 1L || file.Length > limit then
            invalidOp "Signed publication evidence is unavailable."

        File.ReadAllBytes(path)

    let currentAt (now: DateTimeOffset) : TrustedRestorePublication option =
        match reviewedRootKey () with
        | None -> None
        | Some root ->
            try
                let manifestPath, signaturePath = fixedFiles ()
                let manifest = readBounded 16384L manifestPath
                let signature = readBounded 64L signaturePath
                let assembly = typeof<TrustedRestorePublication>.Assembly.Location
                use binary = File.OpenRead(assembly)
                let digest = SHA256.HashData(binary) |> Convert.ToHexStringLower
                verifyWithRoot root manifest signature digest now
            with _ ->
                None

    let current () = currentAt DateTimeOffset.UtcNow
