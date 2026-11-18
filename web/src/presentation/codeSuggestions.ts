import { codeVocabularies } from "@contracts/codes";
export type CodeSuggestion = { code: string; name: string };

/** Eligibility is Core-owned; Intl supplies display names only. */
export const codeSuggestions = (locale: string, currency: boolean): CodeSuggestion[] => {
  const names = new Intl.DisplayNames(locale, {
    type: currency ? "currency" : "region",
    fallback: "none",
  });
  const codes = currency ? codeVocabularies.currency.codes : codeVocabularies.country.codes;
  return codes.map((code) => ({ code, name: names.of(code) ?? code }));
};

export const rankCodeSuggestions = (
  items: ReadonlyArray<CodeSuggestion>,
  query: string,
  locale: string,
): CodeSuggestion[] => {
  const folded = query.toLocaleLowerCase(locale);
  if (folded === "") {
    return [...items];
  }
  const score = (item: CodeSuggestion) => {
    const code = item.code.toLocaleLowerCase(locale);
    const name = item.name.toLocaleLowerCase(locale);
    if (code === folded) {
      return 0;
    }
    if (code.startsWith(folded)) {
      return 1;
    }
    if (name.startsWith(folded)) {
      return 2;
    }
    return `${code} ${name}`.includes(folded) ? 3 : 4;
  };
  return items
    .map((item) => ({ item, rank: score(item) }))
    .filter((value) => value.rank < 4)
    .sort(
      (left, right) =>
        left.rank - right.rank ||
        left.item.name.localeCompare(right.item.name, locale) ||
        left.item.code.localeCompare(right.item.code),
    )
    .map((value) => value.item);
};
