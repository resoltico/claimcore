import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import hashes from "./react-aria-layout-hashes.mjs";

const notice = "/*! ClaimCore modifies React Aria to use external CSS for workspace controls. */";

/** @param {string} id */
const modulePath = (id) =>
  /\/node_modules\/((?:react-aria|react-aria-components)\/dist\/private\/[^?]+)\.mjs(?:\?.*)?$/u.exec(
    id.replaceAll("\\", "/"),
  )?.[1];

/**
 * Move only the locked modules' layout effects. Keep the interaction algorithms in React Aria.
 * @param {string} path
 * @param {string} source
 */
export const adaptAriaLayout = (path, source) => {
  const expected = hashes[/** @type {keyof typeof hashes} */ (path)];
  if (!expected || createHash("sha256").update(source).digest("hex") !== expected) {
    throw new Error(`Review the React Aria layout adaptation for ${path} before updating it.`);
  }

  switch (path.split("/").at(-1)) {
    case "useOverlayPosition":
      return source
        .replace("overlay.style.top = '0px';", 'overlay.setAttribute("data-cc-top", "0");')
        .replace("overlay.style.bottom = '';", 'overlay.removeAttribute("data-cc-bottom");')
        .replace(
          "overlay.style.maxHeight = (window.visualViewport?.height ?? window.innerHeight) + 'px';",
          'overlay.setAttribute("data-cc-max-height", window.visualViewport?.height ?? window.innerHeight);',
        )
        .replace(
          /overlay\.style\.(top|bottom|left|right) = '';/gu,
          'overlay.removeAttribute("data-cc-$1");',
        )
        .replace(
          "Object.keys(position.position).forEach((key)=>overlay.style[key] = position.position[key] + 'px');",
          `for (const key of ["top", "bottom", "left", "right"]) {
            const offset = position.position[key];
            if (Number.isFinite(offset)) overlay.setAttribute("data-cc-" + key, offset);
          }`,
        )
        .replace(
          "overlay.style.maxHeight = position.maxHeight != null ? position.maxHeight + 'px' : '';",
          `if (Number.isFinite(position.maxHeight)) overlay.setAttribute("data-cc-max-height", position.maxHeight);
          else overlay.removeAttribute("data-cc-max-height");
          overlay.setAttribute("data-cc-positioned", "");`,
        )
        .replace(
          /overlayProps: \{\n            style: \{[\s\S]*?\n            \}\n        \},/u,
          'overlayProps: {"data-cc-overlay": ""},',
        );
    case "Popover":
      return source
        .replace(
          /    let style = \{\n        \.\.\.popoverProps\.style,[\s\S]*?\n    \};/u,
          `    const {style: widthStyle, ...elementProps} = renderProps;
    const width = parseFloat(widthStyle?.["--trigger-width"] || triggerWidth);`,
        )
        .replace(
          "        ...renderProps,\n        id: isDialog",
          "        ...elementProps,\n        id: isDialog",
        )
        .replace(
          "        style: style,",
          '        "data-cc-trigger-width": Number.isFinite(width) ? width : undefined,',
        )
        .replace(
          /style: \{\n            position: 'fixed',\n            inset: 0\n        \}/u,
          '"data-cc-underlay": ""',
        )
        .replace(
          /style: \{\n            display: 'contents'\n        \}/u,
          '"data-cc-portal-group": ""',
        );
    case "Modal":
      // The application uses viewport units rather than the optional upstream size variables.
      return source
        .replace(/    let style = \{\n        \.\.\.renderProps\.style,[\s\S]*?\n    \};/u, "")
        .replace("        style: style,\n", "");
    case "DismissButton":
      return source.replace(
        /style: \{\n            width: 1,\n            height: 1\n        \}/u,
        '"data-cc-dismiss": ""',
      );
    case "VisuallyHidden":
      return source
        .replace(/    let combinedStyles = [\s\S]*?\n    \]\);/u, "")
        .replace(
          "style: combinedStyles",
          '"data-cc-visually-hidden": isFocused ? undefined : style?.position === "fixed" ? "fixed" : "absolute"',
        );
    case "useDateField":
      return source.replace(
        /style: \{\n                unicodeBidi: 'isolate'\n            \}/u,
        '"data-cc-date-field": ""',
      );
    case "useDateSegment":
      return source.replace(
        "style: segmentStyle,",
        `dir: segmentStyle.direction,
            "data-cc-date-segment": segmentStyle.unicodeBidi ?? "",`,
      );
    case "useDescription":
      return source.replace("node.style.display = 'none';", "node.hidden = true;");
    case "animation":
      return source.replace(
        /        if \(!isReady && ref.current\) return [^\n]*;/u,
        `        if (!isReady && ref.current) {
            const element = ref.current;
            element.setAttribute("data-cc-enter-pending", "");
            return () => element.removeAttribute("data-cc-enter-pending");
        }`,
      );
    case "usePreventScroll":
      return source
        .replace(
          /    let scrollbarWidth = [^\n]*;\n    return [\s\S]*?(?=UNSTABLE_overrideFocus && .*overrideFocus)/u,
          `    const root = document.documentElement;
    root.setAttribute("data-cc-scroll-lock", "");
    return (0, $a4e76a5424781910$export$e08e3b67e392101e)(() => root.removeAttribute("data-cc-scroll-lock"), `,
        )
        .replace(
          /    let restoreOverflow = [^\n]*;/u,
          `    const root = document.documentElement;
    root.setAttribute("data-cc-scroll-lock", "touch");`,
        )
        .replace(
          /    let style = document\.createElement\('style'\);[\s\S]*?document\.head\.prepend\(style\);/u,
          "",
        )
        .replace(
          "        restoreOverflow();",
          '        root.removeAttribute("data-cc-scroll-lock");',
        )
        .replace("        style.remove();", "");
    case "textSelection":
      return source
        .replace(
          /            [^\n]*savedUserSelect = documentObject\.documentElement\.style\.webkitUserSelect;\n            documentObject\.documentElement\.style\.webkitUserSelect = 'none';/u,
          '            documentObject.documentElement.setAttribute("data-cc-no-selection", "");',
        )
        .replace(
          /        let property = 'userSelect' in target\.style[^\n]*\n        [^\n]*modifiedElementMap\.set[^\n]*\n        target\.style\[property\] = 'none';/u,
          '        target.setAttribute("data-cc-no-selection", "");',
        )
        .replace(
          /                    if \(documentObject\.documentElement\.style\.webkitUserSelect[^\n]*\n                    [^\n]*savedUserSelect = '';/u,
          '                    documentObject.documentElement.removeAttribute("data-cc-no-selection");',
        )
        .replace(
          /        if \(target && [^\n]*modifiedElementMap\.has\(target\)\) \{[\s\S]*?\n        \}/u,
          '        target.removeAttribute("data-cc-no-selection");',
        );
    default:
      throw new Error(`Missing React Aria adaptation for ${path}.`);
  }
};

/** @returns {import("vite").Plugin} */
export default function reactAriaLayout() {
  return {
    name: "claimcore-react-aria-layout",
    enforce: "pre",
    transform(source, id) {
      const path = modulePath(id);
      if (path === undefined || !Object.hasOwn(hashes, path)) {
        return null;
      }
      const file = id.replace(/\?.*$/u, "");
      const upstream = readFileSync(file, "utf8");
      const directive = `//# sourceMappingURL=${path.split("/").at(-1)}.mjs.map`;
      if (
        source !== upstream &&
        source !== upstream.replace(directive, " ".repeat(directive.length))
      ) {
        throw new Error(`Unexpected React Aria transform input for ${path}.`);
      }
      return { code: adaptAriaLayout(path, upstream), map: null };
    },
  };
}

/** @param {{moduleIds: readonly string[]}} chunk */
export const reactAriaLayoutBanner = (chunk) =>
  chunk.moduleIds.some((id) => Object.hasOwn(hashes, modulePath(id) ?? "")) ? notice : "";
