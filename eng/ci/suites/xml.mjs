// A small, strict reader for machine-written XML such as TRX reports: elements, attributes and
// nesting only. Text content is ignored, entities are decoded in attribute values, and a document
// type declaration, a processing instruction beyond the XML declaration or malformed nesting is an
// error, so the reader never expands anything an author could abuse.

/**
 * @typedef {object} XmlElement
 * @property {string} name
 * @property {Record<string, string>} attributes
 * @property {XmlElement[]} children
 */

const maximumBytes = 16 * 1024 * 1024;
const named = /** @type {Record<string, string>} */ ({
  amp: "&",
  lt: "<",
  gt: ">",
  quot: '"',
  apos: "'",
});

/** @param {string} value */
function decode(value) {
  return value.replace(/&(#x[0-9a-fA-F]+|#[0-9]+|[a-z]+);/gu, (match, body) => {
    if (body.startsWith("#x")) {
      return String.fromCodePoint(Number.parseInt(body.slice(2), 16));
    }
    if (body.startsWith("#")) {
      return String.fromCodePoint(Number.parseInt(body.slice(1), 10));
    }
    const replacement = named[body];
    if (replacement === undefined) {
      throw new Error(`Unknown XML entity ${match}.`);
    }
    return replacement;
  });
}

/**
 * @param {string} text
 * @param {number} from Index just after the tag name.
 * @returns {{ attributes: Record<string, string>, end: number, selfClosing: boolean }}
 */
function readTag(text, from) {
  /** @type {Record<string, string>} */
  const attributes = Object.create(null);
  let index = from;
  while (index < text.length) {
    const rest = text.slice(index);
    const space = /^\s+/u.exec(rest);
    if (space) {
      index += space[0].length;
      continue;
    }
    if (rest.startsWith("/>")) {
      return { attributes, end: index + 2, selfClosing: true };
    }
    if (rest.startsWith(">")) {
      return { attributes, end: index + 1, selfClosing: false };
    }
    const attribute = /^([A-Za-z_][\w:.-]*)\s*=\s*(?:"([^"]*)"|'([^']*)')/u.exec(rest);
    if (!attribute?.[1]) {
      throw new Error("Malformed XML tag.");
    }
    if (Object.hasOwn(attributes, attribute[1])) {
      throw new Error(`Duplicate XML attribute ${attribute[1]}.`);
    }
    attributes[attribute[1]] = decode(attribute[2] ?? attribute[3] ?? "");
    index += attribute[0].length;
  }
  throw new Error("Unterminated XML tag.");
}

/**
 * Skip a comment or CDATA section starting at `index`; null when neither starts there.
 * @param {string} text
 * @param {number} index
 * @returns {number | null}
 */
function skipSpecial(text, index) {
  for (const [open, close] of [
    ["<!--", "-->"],
    ["<![CDATA[", "]]>"],
  ]) {
    if (open && close && text.startsWith(open, index)) {
      const end = text.indexOf(close, index + open.length);
      if (end < 0) {
        throw new Error("Unterminated XML section.");
      }
      return end + close.length;
    }
  }
  return null;
}

/**
 * The next markup construct at or after `from`: the text index of its `<`, or -1 at the end.
 * @param {string} text
 * @param {number} from
 * @returns {number}
 */
function nextMarkup(text, from) {
  let index = text.indexOf("<", from);
  while (index >= 0) {
    const skipped = skipSpecial(text, index);
    if (skipped === null) {
      return index;
    }
    index = text.indexOf("<", skipped);
  }
  return index;
}

/**
 * @param {string} text
 * @param {number} at Index of the `</`.
 * @param {XmlElement | undefined} open The element that must close here.
 * @returns {number} Index after the closing tag.
 */
function closeTag(text, at, open) {
  const close = /^<\/([A-Za-z_][\w:.-]*)\s*>/u.exec(text.slice(at));
  if (!close || open?.name !== close[1]) {
    throw new Error("XML closing tag does not match its opening tag.");
  }
  return at + close[0].length;
}

/**
 * @param {string} text
 * @param {number} at Index of the `<`.
 * @returns {{ element: XmlElement, end: number, selfClosing: boolean }}
 */
function openTag(text, at) {
  const name = /^<([A-Za-z_][\w:.-]*)/u.exec(text.slice(at));
  if (!name?.[1]) {
    throw new Error("Malformed XML element.");
  }
  const { attributes, end, selfClosing } = readTag(text, at + name[0].length);
  return { element: { name: name[1], attributes, children: [] }, end, selfClosing };
}

/**
 * Skip a bare `<!DOCTYPE name SYSTEM "..." >` declaration. A declaration with an internal subset
 * could define entities, so it is refused even when doctypes are tolerated.
 * @param {string} text
 * @param {number} at
 * @returns {number} Index after the declaration.
 */
function skipDoctype(text, at) {
  const end = text.indexOf(">", at);
  if (end < 0 || text.slice(at, end).includes("[")) {
    throw new Error("XML document type declarations with an internal subset are not accepted.");
  }
  return end + 1;
}

/**
 * Skip the version header, and a bare document type when tolerated.
 * @param {string} text
 * @param {number} at Index of the `<?` or `<!`.
 * @param {boolean} allowDoctype
 * @returns {number} Index after the declaration.
 */
function declaration(text, at, allowDoctype) {
  if (/^<\?xml[ \t\r\n]/u.test(text.slice(at))) {
    if (at !== 0) {
      throw new Error("XML version declaration must occur at the document start.");
    }
    const end = text.indexOf("?>", at);
    if (end < 0) {
      throw new Error("Unterminated XML version declaration.");
    }
    return end + 2;
  }
  if (allowDoctype && text.startsWith("<!DOCTYPE", at)) {
    return skipDoctype(text, at);
  }
  throw new Error("XML declarations other than the version header are not accepted.");
}

/**
 * Attach an opened element to its parent, or make it the document element.
 * @param {XmlElement | undefined} parent
 * @param {XmlElement | undefined} root
 * @param {XmlElement} element
 * @returns {XmlElement} The document element.
 */
function attach(parent, root, element) {
  if (parent) {
    parent.children.push(element);
    return root ?? element;
  }
  if (root) {
    throw new Error("XML has more than one document element.");
  }
  return element;
}

/**
 * @param {string} source
 * @param {{ allowDoctype?: boolean }} [options] Tolerate an external-only document type.
 * @returns {XmlElement} The document element.
 */
export function parseXml(source, { allowDoctype = false } = {}) {
  if (Buffer.byteLength(source) > maximumBytes) {
    throw new Error("XML document exceeds its bounded size.");
  }
  const text = source.replace(/^\uFEFF/u, "");
  /** @type {XmlElement[]} */
  const stack = [];
  /** @type {XmlElement | undefined} */
  let root;
  for (let at = nextMarkup(text, 0); at >= 0; at = nextMarkup(text, at)) {
    if (text.startsWith("<?", at) || text.startsWith("<!", at)) {
      at = declaration(text, at, allowDoctype);
    } else if (text.startsWith("</", at)) {
      at = closeTag(text, at, stack.pop());
    } else {
      const { element, end, selfClosing } = openTag(text, at);
      root = attach(stack.at(-1), root, element);
      if (!selfClosing) {
        stack.push(element);
      }
      at = end;
    }
  }
  if (!root || stack.length > 0) {
    throw new Error("XML document is incomplete.");
  }
  return root;
}

/**
 * @param {XmlElement} element
 * @param {string} name
 * @returns {XmlElement[]}
 */
export const childrenNamed = (element, name) =>
  element.children.filter((child) => child.name === name);

/**
 * Every descendant element with `name`, in document order.
 * @param {XmlElement} element
 * @param {string} name
 * @returns {XmlElement[]}
 */
export function descendantsNamed(element, name) {
  return element.children.flatMap((child) => [
    ...(child.name === name ? [child] : []),
    ...descendantsNamed(child, name),
  ]);
}
