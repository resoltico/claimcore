namespace ClaimCore.Database

[<NoEquality; NoComparison>]
type internal SignedFencedTailEvidence =
    {
        Report: byte array
        ReportSignature: byte array
        Fence: byte array
        FenceSignature: byte array
        Supplement: byte array
        SupplementSignature: byte array
    }
