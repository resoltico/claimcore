/**
 * The index just past the string that starts at `start`.
 * @param {string} text
 * @param {number} start
 */
function endOfString(text, start) {
  let index = start + 1;
  while (index < text.length && text[index] !== '"') index += text[index] === "\\" ? 2 : 1;
  return index + 1;
}

/**
 * The index just past the comment that starts at `start`, or `start` when there is none.
 * @param {string} text
 * @param {number} start
 */
function endOfComment(text, start) {
  const pair = text.slice(start, start + 2);
  if (pair === "//") {
    const end = text.indexOf("\n", start);
    return end < 0 ? text.length : end;
  }
  if (pair === "/*") {
    const end = text.indexOf("*/", start + 2);
    return end < 0 ? text.length : end + 2;
  }
  return start;
}

/**
 * Parse JSON with comments and trailing commas, as oxlint and TypeScript configuration files allow.
 * @param {string} text
 * @returns {unknown}
 */
export function parseJsonc(text) {
  let output = "";
  let index = 0;
  while (index < text.length) {
    const end = text[index] === '"' ? endOfString(text, index) : endOfComment(text, index);
    if (end === index) {
      output += text[index];
      index += 1;
    } else {
      if (text[index] === '"') output += text.slice(index, end);
      index = end;
    }
  }
  return JSON.parse(output.replace(/,(\s*[\]}])/g, "$1"));
}
