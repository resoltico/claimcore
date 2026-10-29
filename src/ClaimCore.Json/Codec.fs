namespace ClaimCore.Json

/// Public codec construction stays together; implementation parts follow grammar responsibility.
module Codec =
    let text = CodecScalars.text

    let boundedText minimum maximum =
        CodecScalars.boundedText minimum maximum

    let named name codec = CodecScalars.named name codec
    let vocabulary name codec = CodecScalars.vocabulary name codec
    let uuid = CodecScalars.uuid
    let integer minimum maximum = CodecScalars.integer minimum maximum
    let boolean = CodecScalars.boolean
    let instant = CodecScalars.instant
    let enumeration cases = CodecScalars.enumeration cases
    let nullable codec = CodecScalars.nullable codec
    let list maximum codec = CodecScalars.list maximum codec
    let map read write codec = CodecScalars.map read write codec
    let mapResult read write codec = CodecScalars.mapResult read write codec

    let constrained schema accepts codec =
        CodecScalars.constrained schema accepts codec

    let boundedList minimum maximum codec =
        CodecScalars.boundedList minimum maximum codec

    let constant token = CodecScalars.constant token
    let record constructor = CodecRecords.record constructor

    let field name get codec fields =
        CodecRecords.field name get codec fields

    let optionalField name get codec fields =
        CodecRecords.optionalField name get codec fields

    let build fields = CodecRecords.build fields
    let fieldMap members = CodecRecords.fieldMap members
    let flatten get part fields = CodecRecords.flatten get part fields

    let buildResult describe fields =
        CodecRecords.buildResult describe fields

    let variant tag constructor project fields =
        CodecUnions.variant tag constructor project fields

    let unitVariant tag value = CodecUnions.unitVariant tag value

    let extendVariant constructor project extra source =
        CodecUnions.extendVariant constructor project extra source

    let taggedOn memberName name variants =
        CodecUnions.taggedOn memberName name variants

    let tagged name variants = CodecUnions.tagged name variants

    let groupedTaggedOn memberName name variants =
        CodecUnions.groupedTaggedOn memberName name variants

    let nullValue = CodecUnions.nullValue

    let branch constructor project codec =
        CodecUnions.branch constructor project codec

    let choice name variants = CodecUnions.choice name variants
    let withFormat format codec = CodecUnions.withFormat format codec
    let encode (codec: Codec<'T>) value = codec.Write value |> WireWriter.bytes

    let decode maximumBytes (codec: Codec<'T>) bytes =
        StrictJson.read maximumBytes bytes |> Result.bind (codec.Read "$")

    let writeSchema prefix writer schema =
        CodecSchema.writeSchema prefix writer schema
