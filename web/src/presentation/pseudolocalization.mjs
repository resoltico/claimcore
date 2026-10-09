import { TYPE } from "@formatjs/icu-messageformat-parser";

/** @param {string} value */
const expand = (value) =>
  value.replace(
    /[aeiouAEIOU]/gu,
    (c) =>
      ({
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
      })[/** @type {"a"|"e"|"i"|"o"|"u"|"A"|"E"|"I"|"O"|"U"} */ (c)],
  );
/** @param {import("@formatjs/icu-messageformat-parser").MessageFormatElement[]} ast @returns {import("@formatjs/icu-messageformat-parser").MessageFormatElement[]} */
export const pseudolocalize = (ast) =>
  ast.map((node) => {
    if (node.type === TYPE.literal) {
      return { ...node, value: expand(node.value) };
    }
    if (node.type === TYPE.plural || node.type === TYPE.select) {
      return {
        ...node,
        options: Object.fromEntries(
          Object.entries(node.options).map(([k, v]) => [
            k,
            { ...v, value: pseudolocalize(v.value) },
          ]),
        ),
      };
    }
    return structuredClone(node);
  });
