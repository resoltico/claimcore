import { readFileSync, readdirSync } from "node:fs";
import { resolve, relative } from "node:path";
import postcss from "postcss";
// CSS named colours are literals too; system colours, transparent and currentColor are intentional.
const namedColors = new Set(
  "aliceblue antiquewhite aqua aquamarine azure beige bisque black blanchedalmond blue blueviolet brown burlywood cadetblue chartreuse chocolate coral cornflowerblue cornsilk crimson cyan darkblue darkcyan darkgoldenrod darkgray darkgreen darkgrey darkkhaki darkmagenta darkolivegreen darkorange darkorchid darkred darksalmon darkseagreen darkslateblue darkslategray darkslategrey darkturquoise darkviolet deeppink deepskyblue dimgray dimgrey dodgerblue firebrick floralwhite forestgreen fuchsia gainsboro ghostwhite gold goldenrod gray green greenyellow grey honeydew hotpink indianred indigo ivory khaki lavender lavenderblush lawngreen lemonchiffon lightblue lightcoral lightcyan lightgoldenrodyellow lightgray lightgreen lightgrey lightpink lightsalmon lightseagreen lightskyblue lightslategray lightslategrey lightsteelblue lightyellow lime limegreen linen magenta maroon mediumaquamarine mediumblue mediumorchid mediumpurple mediumseagreen mediumslateblue mediumspringgreen mediumturquoise mediumvioletred midnightblue mintcream mistyrose moccasin navajowhite navy oldlace olive olivedrab orange orangered orchid palegoldenrod palegreen paleturquoise palevioletred papayawhip peachpuff peru pink plum powderblue purple rebeccapurple red rosybrown royalblue saddlebrown salmon sandybrown seagreen seashell sienna silver skyblue slateblue slategray slategrey snow springgreen steelblue tan teal thistle tomato turquoise violet wheat white whitesmoke yellow yellowgreen".split(
    " ",
  ),
);
/** @param {string} text */
const hasNamedColor = (text) =>
  [
    ...text.matchAll(
      /\b(?:color|background(?:-color)?|border(?:-[\w-]+)?|fill|stroke)\s*:\s*([^;{}\n]+)/gu,
    ),
  ].some(
    ([, value]) =>
      value !== undefined &&
      [...value.matchAll(/\b[a-z]+\b/giu)].some(([word]) => namedColors.has(word.toLowerCase())),
  );
const sizingProperty =
  /^(?:margin(?:-[\w-]+)?|padding(?:-[\w-]+)?|(?:(?:row|column)-)?gap|inset(?:-[\w-]+)?|(?:min-|max-)?(?:width|height)|[\w-]+-size)$/u;

const valueKeywords = /^(?:0|auto|(?:fit|min|max)-content|none|-?\d*\.?\d+(?:%|fr))$/u;

/** @param {string} value */
const usesSizingTokens = (value) => {
  const withoutTokens = value.replace(/var\(--[\w-]+(?:\s*,\s*(?:0|auto))?\)/gu, "0");
  const withoutCalculations = withoutTokens.replace(/calc\(([^()]+)\)/gu, (expression, body) => {
    const operatorsAndNumbers = /^[\s+*/().0-9%-]+$/u;
    return operatorsAndNumbers.test(body) ? "0" : expression;
  });
  return withoutCalculations
    .trim()
    .split(/\s+/u)
    .every((token) => valueKeywords.test(token));
};

/** @param {import("postcss").Node | undefined} node */
const layerName = (node) =>
  node?.type === "atrule" &&
  "name" in node &&
  node.name === "layer" &&
  "params" in node &&
  typeof node.params === "string"
    ? node.params
    : undefined;

/** @param {import("postcss").Rule} rule */
const containingLayer = (rule) => {
  /** @type {import("postcss").Node | undefined} */
  let { parent } = rule;
  while (parent !== undefined && layerName(parent) === undefined) {
    ({ parent } = parent);
  }
  return parent;
};

/**
 * @param {import("postcss").Root} sheet
 * @param {string} path
 * @param {ReadonlySet<string>} layers
 * @param {Map<string, string>} selectors
 * @param {(reason: string) => void} add
 */
const inspectCss = (sheet, path, layers, selectors, add) => {
  sheet.walkRules((rule) => {
    const layer = containingLayer(rule);
    if (!layers.has(layerName(layer) ?? "")) {
      add(`rule is outside a declared cascade layer: ${rule.selector}`);
    }
    if (rule.parent === layer) {
      for (const selector of rule.selectors) {
        const first = selectors.get(selector);
        if (first === undefined) {
          selectors.set(selector, path);
        } else {
          add(`repeated top-level selector ${selector} (first in ${first})`);
        }
      }
    }
    rule.walkDecls(/^(overflow-wrap|word-wrap)$/iu, (decl) => {
      if (decl.value.trim().toLowerCase() !== "anywhere") {
        return;
      }
      if (!rule.selectors.every((selector) => selector === ".identifier-value")) {
        add(`word wrapping anywhere is restricted to .identifier-value: ${rule.selector}`);
      }
    });
    if (/\[data-(?:code|headline|act)\s*=/u.test(rule.selector)) {
      add(`domain identity selector: ${rule.selector}`);
    }
  });
  if (path !== "ui/layout.css" && !path.startsWith("theme/")) {
    sheet.walkDecls((decl) => {
      if (sizingProperty.test(decl.prop) && !usesSizingTokens(decl.value)) {
        add(`spacing or sizing must use tokens: ${decl.prop}: ${decl.value}`);
      }
    });
  }
};

/**
 * @param {string} path
 * @param {string} text
 * @param {(reason: string) => void} add
 */
const inspectControls = (path, text, add) => {
  if (!path.startsWith("ui/") && /<(?:input|textarea|select|small)(?:\s|\/?>)/u.test(text)) {
    add("form controls and small elements belong in src/ui");
  }
  if (/<select(?:\s|>)/u.test(text) || /["']react-aria-components\/Select["']/u.test(text)) {
    add("native select and HiddenSelect violate qualified WebKit CSP; use ui/Select");
  }
};

/**
 * @param {string} path
 * @param {string} text
 * @param {(reason: string) => void} add
 */
const inspectSource = (path, text, add) => {
  inspectControls(path, text, add);
  if (/(?:#[\da-f]{3,8}\b|\b(?:rgb|rgba|hsl|hsla|oklch|oklab|lab|lch|color)\s*\()/iu.test(text)) {
    add("colour literals belong in theme/tokens.json");
  }
  if (/\bstyle\s*=|\.style\.(?:setProperty|removeProperty)/u.test(text)) {
    add("inline styles belong in semantic stylesheets");
  }
  if (hasNamedColor(text)) {
    add("named colour literal");
  }
  if (
    !path.startsWith("theme/") &&
    /var\(--(?:neutral|green|blue|red|amber|violet|emerald|sky)-/u.test(text)
  ) {
    add("components must use semantic colours");
  }
  if (path !== "ui/layout.css" && /@media[^{}]*\b(?:width|height)\b/u.test(text)) {
    add("viewport media queries belong in ui/layout.css");
  }
  if (/font-size\s*:\s*[^;{}]*\b[\d.]+px\b/u.test(text)) {
    add("font sizes must use rem or type tokens");
  }
};
/**
 * @param {string} root
 * @param {string} [generatedTheme]
 */
export const designFindings = (root, generatedTheme) => {
  const source = resolve(root, "src");
  const files = readdirSync(source, { recursive: true, encoding: "utf8" }).filter(
    (path) => /\.(css|tsx?|html|svg)$/u.test(path) && path !== "theme/tokens.css",
  );
  files.push("../index.html");
  files.push(
    ...readdirSync(resolve(root, "public"), { recursive: true, encoding: "utf8" })
      .filter((path) => /\.(css|html|svg)$/u.test(path))
      .map((path) => `../public/${path}`),
  );
  /** @type {string[]} */
  const findings = [];
  /** @type {Array<{path: string, sheet: import("postcss").Root}>} */
  const sheets = [];
  for (const path of files) {
    const text = readFileSync(resolve(source, path), "utf8");
    const add = (/** @type {string} */ reason) =>
      findings.push(`${relative(root, resolve(source, path))}: ${reason}`);
    if (path.endsWith(".css")) {
      try {
        sheets.push({ path, sheet: postcss.parse(text, { from: path }) });
      } catch {
        add("CSS could not be parsed");
      }
    }
    inspectSource(path, text, add);
  }
  if (generatedTheme !== undefined) {
    sheets.push({ path: "theme/tokens.css", sheet: postcss.parse(generatedTheme) });
  }
  const layers = new Set(
    sheets
      .flatMap(({ sheet }) => sheet.nodes)
      .filter((node) => node.type === "atrule" && node.name === "layer" && node.nodes === undefined)
      .flatMap((node) => ("params" in node ? postcss.list.comma(node.params) : [])),
  );
  /** @type {Map<string,string>} */
  const selectors = new Map();
  for (const { path, sheet } of sheets) {
    inspectCss(sheet, path, layers, selectors, (reason) => findings.push(`src/${path}: ${reason}`));
  }
  return findings;
};
