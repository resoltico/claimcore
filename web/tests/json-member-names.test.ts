import { expect, it } from "vitest";
import { strictJsonBytes } from "../src/api/responseBytes";

const parse = (source: string) => strictJsonBytes(new TextEncoder().encode(source));

it("refuses duplicate decoded member names at every object scope", () => {
  for (const source of [
    '{"operationId":"wrong","operationId":"requested"}',
    '{"operationId":1,"operation\\u0049d":2}',
    '{"nested":{"a":1,"a":2}}',
    '[{"a":1,"a":2}]',
  ]) {
    expect(() => parse(source)).toThrow("Duplicate JSON member");
  }
});

it("separates array and object scopes and ignores structural text inside string values", () => {
  expect(parse('{"a":{"id":1},"b":{"id":2},"c":[{"id":3},{"id":4}]}')).toEqual({
    a: { id: 1 },
    b: { id: 2 },
    c: [{ id: 3 }, { id: 4 }],
  });
  expect(parse('{"value":"\\\"key\\\":1,{[]}","empty":{},"array":["x",null]}')).toEqual({
    value: '"key":1,{[]}',
    empty: {},
    array: ["x", null],
  });
});

it("retains JSON.parse grammar refusal for incomplete strings, braces and non-JSON whitespace", () => {
  for (const source of ['{"a":"unfinished', '{"a":1]', '{"a":1,}', '{"a"\u00a0:1}']) {
    expect(() => parse(source)).toThrow();
  }
});
