namespace ClaimCore.Docs

type BaselineAssessment =
    {
        Identities: Set<string>
        SourceCount: int
        Total: int
    }

type LineageAssessment =
    {
        BaselineSha256: string
        Entries: Map<string, string list>
    }

type MatrixAssessment =
    {
        BaselineSha256: string
        EntryIds: Set<string>
        EntryTests: Map<string, string list>
        OutcomeTags: Map<string, string list>
        Tests: Set<string>
    }
