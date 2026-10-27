namespace ClaimCore.RecordFormat

module RecordLimits =
    [<Literal>]
    let MaximumRequestRecordBytes = 65536

    let internal requestBytes (bytes: byte array) =
        if bytes.Length > MaximumRequestRecordBytes then
            invalidOp "The encoded request exceeds the permanent record-format byte limit."

        bytes
