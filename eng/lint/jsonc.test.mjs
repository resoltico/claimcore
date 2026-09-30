import assert from "node:assert/strict";
import test from "node:test";
import { parseJsonc } from "./jsonc.mjs";

test("comments and trailing commas are accepted", () => {
  assert.deepEqual(parseJsonc('{ // line\n "a": [1, 2,], /* block */ "b": {"c": true,}, }'), {
    a: [1, 2],
    b: { c: true },
  });
});

test("comment markers and escaped quotes inside strings are data", () => {
  assert.deepEqual(parseJsonc('{ "url": "https://example.test/*x*/", "q": "say \\"//\\"" }'), {
    url: "https://example.test/*x*/",
    q: 'say "//"',
  });
});

test("an unterminated block comment ends the document instead of looping", () => {
  assert.deepEqual(parseJsonc('{ "a": 1 } /* never closed'), { a: 1 });
});

test("invalid JSON still throws", () => {
  assert.throws(() => parseJsonc("{ a: 1 }"));
});
