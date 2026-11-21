namespace ClaimCore.Application.Storage

/// Escape a literal fragment for LIKE with backslash as the escape character.
module internal LikePattern =
    let escapeLiteral (value: string) =
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_")
