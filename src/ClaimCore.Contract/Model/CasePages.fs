namespace ClaimCore.Contract

[<RequireQualifiedAccess>]
type CaseSummary =
    | Live of
        caseReference: string *
        revision: int64 *
        status: CaseStatus *
        disposition: Disposition
    | Erased of caseReference: string * revision: int64

type CaseSummaryPage =
    {
        Items: CaseSummary list
        Offers: InstallationOffer list
        NextCursor: string option
    }
