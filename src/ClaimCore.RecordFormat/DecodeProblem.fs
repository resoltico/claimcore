namespace ClaimCore.RecordFormat

/// Fixed record-decoding reasons contain no retained payload or submitted values.
type DecodeProblem = { Field: string; Message: string }
