import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import Ajv2020 from "ajv/dist/2020.js";
import addFormats from "ajv-formats";
import { validatorName } from "./validator-groups.mjs";

const directory = new URL("../src/generated/contracts/", import.meta.url);
const read = async (name) => JSON.parse(await readFile(new URL(name, directory), "utf8"));
const catalogue = await read("catalog.json");
const corpus = await read("endpoint-corpus.json");
const requests = new Map();
const responses = new Map();
const compiler = new Ajv2020({
  strict: true,
  coerceTypes: false,
  removeAdditional: false,
  useDefaults: false,
  validateFormats: true,
});
addFormats(compiler);
for (const endpoint of catalogue.endpoints) {
  requests.set(endpoint.id, compiler.compile(await read(endpoint.requestSchema)));
  let group = "core";
  if (endpoint.id === "definition") {
    group = "discovery";
  } else if (endpoint.id.startsWith("authority.")) {
    group = "authority";
  }
  const module = await import(new URL(`validators.${group}.mjs`, directory));
  responses.set(endpoint.id, module[validatorName(endpoint.id)]);
}

test("native request and response corpora agree with strict JavaScript and standalone validators", () => {
  assert.equal(catalogue.endpoints.length, 14);
  const reached = new Set();
  let positive = 0;
  let negative = 0;
  for (const sample of corpus.cases) {
    const validator = (sample.direction === "request" ? requests : responses).get(sample.endpoint);
    assert.equal(typeof validator, "function", sample.id);
    assert.equal(validator(sample.value), sample.valid, sample.id);
    reached.add(`${sample.endpoint}:${sample.direction}:${sample.valid}`);
    if (sample.valid) {
      positive++;
    } else {
      negative++;
    }
  }
  assert.equal(positive, 100);
  assert.equal(negative, 99);
  assert.equal(reached.size, 56);
});
