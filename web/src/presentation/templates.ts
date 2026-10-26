export type TemplatePart = Readonly<{ kind: "literal" | "hole"; value: string }>;

/** Interpret generator-admitted Template text: literals, doubled apostrophes and named holes. */
export const templateParts = (source: string): ReadonlyArray<TemplatePart> => {
  const parts: TemplatePart[] = [];
  let literal = "";
  const flush = () => {
    if (literal !== "") {
      parts.push({ kind: "literal", value: literal });
    }
    literal = "";
  };
  for (let i = 0; i < source.length; i++) {
    const character = source[i]!;
    if (character === "'" && source[i + 1] === "'") {
      literal += "'";
      i++;
    } else if (character === "'" && ["{", "}"].includes(source[i + 1] ?? "")) {
      for (i++; i < source.length; i++) {
        if (source[i] !== "'") {
          literal += source[i];
        } else if (source[i + 1] === "'") {
          literal += "'";
          i++;
        } else {
          break;
        }
      }
    } else if (character === "{") {
      flush();
      const end = source.indexOf("}", i + 1);
      parts.push({ kind: "hole", value: source.slice(i + 1, end).trim() });
      i = end;
    } else {
      literal += character;
    }
  }
  flush();
  return parts;
};

const expandedVowels: Readonly<Record<string, string>> = {
  a: "àà",
  e: "ëë",
  i: "ïï",
  o: "öö",
  u: "üü",
  A: "ÀÀ",
  E: "ËË",
  I: "ÏÏ",
  O: "ÖÖ",
  U: "ÜÜ",
};
export const pseudolocalizeLiteral = (value: string): string =>
  value.replace(/[aeiouAEIOU]/gu, (character) => expandedVowels[character] ?? character);
