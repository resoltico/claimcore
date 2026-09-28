namespace ClaimCore.Postgres

open System
open System.IO
open System.Text.Json

module internal BackupHealthPolicyCodec =
    open BackupHealthFields

    let private roleFields =
        [
            "role"
            "publicKeyBase64"
            "machineSha256"
            "storageSha256"
            "adminActorId"
            "signerHolderActorId"
        ]

    let private role (value: JsonElement) =
        if not (BackupHealthCanonical.exact value roleFields) then
            invalidOp "Backup health custodian pin is malformed."

        let publicKey = Convert.FromBase64String(text value "publicKeyBase64")

        if publicKey.Length <> 32 || not (ManagedCopySignature.validPublicKey publicKey) then
            invalidOp "Backup health custodian key is invalid."

        {
            Role = text value "role"
            PublicKey = publicKey
            MachineSha256 = sha value "machineSha256"
            StorageSha256 = sha value "storageSha256"
            AdminActorId = uuid value "adminActorId"
            SignerHolderActorId = uuid value "signerHolderActorId"
        }

    let private distinct (roles: BackupHealthRolePin list) =
        let unique values =
            values |> List.distinct |> List.length = roles.Length

        let keyHash role =
            Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData(role.PublicKey))

        if
            roles.Length <> 3
            || (roles |> List.map _.Role |> Set.ofList)
               <> set [ "archive"; "checkpoint"; "test-restore" ]
            || not (unique (roles |> List.map keyHash))
            || not (unique (roles |> List.map _.MachineSha256))
            || not (unique (roles |> List.map _.StorageSha256))
            || not (unique (roles |> List.map _.AdminActorId))
            || not (unique (roles |> List.map _.SignerHolderActorId))
            || roles |> List.exists (fun role -> role.AdminActorId = role.SignerHolderActorId)
        then
            invalidOp "Backup health custodian roles are not independent."

    let private fields =
        [
            "format"
            "policyId"
            "backupIntervalSeconds"
            "maximumBackupAgeSeconds"
            "maximumWalLagSeconds"
            "maximumCheckpointAgeSeconds"
            "maximumRestoreTestAgeSeconds"
            "restoreHorizonSeconds"
            "archiveRoot"
            "checkpointRoot"
            "restoreRoot"
            "roles"
        ]

    let private decoded (root: JsonElement) =
        if
            not (BackupHealthCanonical.exact root fields)
            || text root "format" <> "claimcore-backup-health-policy-1"
        then
            invalidOp "Backup health policy is malformed."

        let maximum = 365L * 86400L
        let rolesValue = root.GetProperty("roles")

        if rolesValue.ValueKind <> JsonValueKind.Array then
            invalidOp "Backup health custodian roster is invalid."

        let roles = rolesValue.EnumerateArray() |> Seq.map role |> Seq.toList
        distinct roles

        let value =
            {
                PolicyId = policyId root
                BackupIntervalSeconds = positive maximum (number root "backupIntervalSeconds")
                MaximumBackupAgeSeconds = positive maximum (number root "maximumBackupAgeSeconds")
                MaximumWalLagSeconds = positive maximum (number root "maximumWalLagSeconds")
                MaximumCheckpointAgeSeconds =
                    positive maximum (number root "maximumCheckpointAgeSeconds")
                MaximumRestoreTestAgeSeconds =
                    positive maximum (number root "maximumRestoreTestAgeSeconds")
                RestoreHorizonSeconds = positive maximum (number root "restoreHorizonSeconds")
                ArchiveRoot = text root "archiveRoot"
                CheckpointRoot = text root "checkpointRoot"
                RestoreRoot = text root "restoreRoot"
                Roles = roles
            }

        if
            value.BackupIntervalSeconds > value.MaximumBackupAgeSeconds
            || value.MaximumBackupAgeSeconds > value.RestoreHorizonSeconds
            || value.MaximumWalLagSeconds > value.RestoreHorizonSeconds
            || value.MaximumCheckpointAgeSeconds > value.RestoreHorizonSeconds
            || value.MaximumRestoreTestAgeSeconds > value.RestoreHorizonSeconds
            || not (Path.IsPathFullyQualified value.ArchiveRoot)
            || not (Path.IsPathFullyQualified value.CheckpointRoot)
            || not (Path.IsPathFullyQualified value.RestoreRoot)
            || value.ArchiveRoot = value.CheckpointRoot
            || value.ArchiveRoot = value.RestoreRoot
            || value.CheckpointRoot = value.RestoreRoot
        then
            invalidOp "Backup health policy or custody location is invalid."

        value

    let parse (source: byte array) expectedSha =
        try
            if
                Convert.ToHexStringLower(Security.Cryptography.SHA256.HashData(source))
                <> expectedSha
            then
                None
            else
                match BackupHealthCanonical.parse 65536 source with
                | None -> None
                | Some root -> Some(decoded root)
        with _ ->
            None
