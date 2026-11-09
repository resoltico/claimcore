import { readFileSync, readdirSync } from "node:fs";
import { resolve, relative } from "node:path";
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
/** @param {string} root */
export const designFindings = (root) => {
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
  for (const path of files) {
    const text = readFileSync(resolve(source, path), "utf8");
    const add = (/** @type {string} */ reason) =>
      findings.push(`${relative(root, resolve(source, path))}: ${reason}`);
    if (/<select(?:\s|>)/u.test(text) || /["']react-aria-components\/Select["']/u.test(text)) {
      add("native select and HiddenSelect violate qualified WebKit CSP; use ui/Select");
    }
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
  }
  return findings;
};
