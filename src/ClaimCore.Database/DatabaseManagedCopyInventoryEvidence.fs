namespace ClaimCore.Database

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

module internal DatabaseManagedCopyInventoryEvidence =
    let private names = DatabaseRestoreCanonical.exactProperties

    let private number = DatabaseRestoreCanonical.number

    let private text name (root: JsonElement) =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Managed-copy location text is invalid.")

    let private id name root =
        let raw = text name root

        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
        | _ -> invalidOp "Managed-copy location identity is invalid."

    let private sha name root =
        let raw = text name root

        if raw.Length <> 64 || raw <> raw.ToLowerInvariant() then
            invalidOp "Managed-copy location digest is invalid."

        let value = Convert.FromHexString(raw)

        if Convert.ToHexStringLower(value) <> raw then
            invalidOp "Managed-copy location digest is invalid."

        value

    let private instant name root =
        let raw = text name root

        DateTimeOffset.ParseExact(
            raw,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
        )

    let private envelope bytes =
        use document =
            DatabaseRestoreCanonical.parse bytes
            |> Option.defaultWith (fun () -> invalidOp "Copy evidence is noncanonical.")

        let root = document.RootElement

        if not (names [ "report"; "signatureBase64" ] root) then
            invalidOp "Copy evidence envelope is invalid."

        let report = root.GetProperty("report")
        let signature = Convert.FromBase64String(text "signatureBase64" root)

        if signature.Length <> 64 then
            invalidOp "Copy evidence signature length is invalid."

        let canonical = Encoding.ASCII.GetBytes(report.GetRawText() + "\n")

        use exact =
            DatabaseRestoreCanonical.parse canonical
            |> Option.defaultWith (fun () -> invalidOp "Copy evidence body is noncanonical.")

        canonical, signature, exact.RootElement.Clone()

    let private entry (root: JsonElement) =
        if
            not (
                names
                    [
                        "copyId"
                        "producerKind"
                        "custodianId"
                        "location"
                        "kind"
                        "sourceCaseId"
                        "ciphertextSha256"
                        "ciphertextBytes"
                    ]
                    root
            )
        then
            invalidOp "Copy location entry is invalid."

        let source = root.GetProperty("sourceCaseId")
        let producer = text "producerKind" root

        let custodian, location = DatabaseManagedCopyLocationPolicy.custody producer root

        let bytes = number "ciphertextBytes" root

        if bytes < 1L || bytes > (1L <<< 40) then
            invalidOp "Copy location entry is unsafe."

        {
            CopyId = id "copyId" root
            ProducerKind = producer
            CustodianId = custodian
            Location = location
            Kind = text "kind" root
            SourceCaseId =
                if source.ValueKind = JsonValueKind.Null then
                    None
                else
                    Some(id "sourceCaseId" root)
            CiphertextSha256 = sha "ciphertextSha256" root
            CiphertextBytes = bytes
        }

    let private observation root =
        if not (names [ "copyId"; "status"; "sha256"; "bytes" ] root) then
            invalidOp "Copy location observation is invalid."

        let status = text "status" root
        let digest = root.GetProperty("sha256")
        let size = root.GetProperty("bytes")

        match status, digest.ValueKind, size.ValueKind with
        | "PRESENT", JsonValueKind.String, JsonValueKind.Number ->
            {
                CopyId = id "copyId" root
                Status = status
                Sha256 = Some(sha "sha256" root)
                Bytes = Some(number "bytes" root)
            }
        | "ABSENT", JsonValueKind.Null, JsonValueKind.Null ->
            {
                CopyId = id "copyId" root
                Status = status
                Sha256 = None
                Bytes = None
            }
        | "UNKNOWN", JsonValueKind.Null, JsonValueKind.Null ->
            {
                CopyId = id "copyId" root
                Status = status
                Sha256 = None
                Bytes = None
            }
        | _ -> invalidOp "Copy location observation is invalid."

    let private boundedArray
        (name: string)
        (maximum: int)
        (parse: JsonElement -> 'a)
        (root: JsonElement)
        =
        let values = root.GetProperty(name)

        if values.ValueKind <> JsonValueKind.Array || values.GetArrayLength() > maximum then
            invalidOp "Copy location inventory bound is invalid."

        values.EnumerateArray() |> Seq.map parse |> Seq.toList

    let private verifyShape (registry: JsonElement) (inspection: JsonElement) =
        if
            not (
                names
                    [
                        "format"
                        "signingKeyId"
                        "installationId"
                        "lineageId"
                        "epoch"
                        "witnessCutoffSequence"
                        "witnessCutoffHash"
                        "issuedAt"
                        "expiresAt"
                        "entries"
                        "knownUnmanaged"
                    ]
                    registry
            )
            || not (
                names
                    [
                        "format"
                        "signingKeyId"
                        "registrySha256"
                        "installationId"
                        "lineageId"
                        "epoch"
                        "witnessCutoffSequence"
                        "witnessCutoffHash"
                        "issuedAt"
                        "expiresAt"
                        "observations"
                    ]
                    inspection
            )
            || text "format" registry <> "claimcore-copy-location-inventory-1"
            || text "format" inspection <> "claimcore-copy-location-inspection-1"
        then
            invalidOp "Copy location evidence format is invalid."

    let private unmanaged (registry: JsonElement) =
        boundedArray
            "knownUnmanaged"
            10000
            (fun value ->
                let raw = value.GetString() |> Option.ofObj |> Option.defaultValue ""

                match Guid.TryParseExact(raw, "D") with
                | true, parsed when parsed <> Guid.Empty && parsed.ToString("D") = raw -> parsed
                | _ -> invalidOp "Known unmanaged copy identity is invalid.")
            registry

    let private idsMatch
        (entries: CopyLocationEntry list)
        (unmanaged: Guid list)
        (observations: CopyObservation list)
        =
        let copies = entries |> List.map _.CopyId |> Set.ofList
        let observed = observations |> List.map _.CopyId |> Set.ofList
        let external = unmanaged |> Set.ofList

        copies.Count = entries.Length
        && observed.Count = observations.Length
        && external.Count = unmanaged.Length
        && copies = observed
        && Set.isEmpty (Set.intersect copies external)

    let private authorityMatches (registry: JsonElement) (inspection: JsonElement) registrySha =
        sha "registrySha256" inspection = registrySha
        && id "installationId" registry = id "installationId" inspection
        && id "lineageId" registry = id "lineageId" inspection
        && number "epoch" registry = number "epoch" inspection
        && number "witnessCutoffSequence" registry = number "witnessCutoffSequence" inspection
        && sha "witnessCutoffHash" registry = sha "witnessCutoffHash" inspection

    let private timeMatches registryIssued registryExpiry observed inspectionExpiry now =
        registryIssued <= observed
        && observed <= now
        && registryExpiry > now
        && inspectionExpiry > now
        && registryExpiry - registryIssued <= TimeSpan.FromMinutes(10.)
        && inspectionExpiry - observed <= TimeSpan.FromMinutes(5.)

    let private verifyMatching
        (registry: JsonElement)
        (inspection: JsonElement)
        (registrySha: byte array)
        (entries: CopyLocationEntry list)
        (unmanaged: Guid list)
        (observations: CopyObservation list)
        (now: DateTimeOffset)
        =
        let registryIssued = instant "issuedAt" registry
        let registryExpiry = instant "expiresAt" registry
        let observed = instant "issuedAt" inspection
        let inspectionExpiry = instant "expiresAt" inspection

        if
            not (idsMatch entries unmanaged observations)
            || not (authorityMatches registry inspection registrySha)
            || not (timeMatches registryIssued registryExpiry observed inspectionExpiry now)
        then
            invalidOp "Copy location evidence is stale or divergent."

        observed, registryExpiry, inspectionExpiry

    let parse registryBytes inspectionBytes now =
        let registryCanonical, registrySignature, registry = envelope registryBytes
        let inspectionCanonical, inspectionSignature, inspection = envelope inspectionBytes
        verifyShape registry inspection
        let registrySha = SHA256.HashData(registryCanonical)
        let entries = boundedArray "entries" 10000 entry registry
        let unmanagedIds = unmanaged registry
        let observations = boundedArray "observations" 10000 observation inspection

        let observed, registryExpiry, inspectionExpiry =
            verifyMatching registry inspection registrySha entries unmanagedIds observations now

        {
            RegistryKeyId = id "signingKeyId" registry
            InspectorKeyId = id "signingKeyId" inspection
            InstallationId = id "installationId" registry
            LineageId = id "lineageId" registry
            Epoch = number "epoch" registry
            CutoffSequence = number "witnessCutoffSequence" registry
            CutoffHash = sha "witnessCutoffHash" registry
            RegistrySha256 = registrySha
            RegistryCanonical = registryCanonical
            RegistrySignature = registrySignature
            InspectionCanonical = inspectionCanonical
            InspectionSignature = inspectionSignature
            RegistryEntries = entries
            KnownUnmanaged = unmanagedIds
            Observations = observations
            ObservedAt = observed
            RegistryExpiresAt = registryExpiry
            InspectionExpiresAt = inspectionExpiry
        }
