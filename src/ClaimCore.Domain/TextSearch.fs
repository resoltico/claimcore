namespace ClaimCore.Domain

module TextSearch =
    let normalize (value: string) =
        value.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant()
