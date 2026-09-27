namespace ClaimCore.Database

module internal DatabaseRestoreProduceCustody =
    let archive (input: RestoreProduceInput) =
        {
            ObjectId = input.Index.ArchiveSetId
            Sha256 = DatabaseRestoreArchiveObjects.rootDigest input.Index.ArchiveObjects
            Bytes = DatabaseRestoreArchiveObjects.totalBytes input.Index.ArchiveObjects
        }

    let checkpoint (input: RestoreProduceInput) bytes digest =
        {
            ObjectId = input.Index.CheckpointObjectId
            Sha256 = digest
            Bytes = bytes
        }
