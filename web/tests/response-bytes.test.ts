import { ReadableStream } from "node:stream/web";
import { expect, it, vi } from "vitest";
import { responseBytes, strictJsonBytes } from "../src/api/responseBytes";
import { v3 } from "../src/api/v3";
import { recoveryArtifact } from "./recovery-artifact.fixtures";
import { operationId } from "./v3-ui.fixtures";

const attachment = (bytes: BodyInit) =>
  new Response(bytes, {
    headers: {
      "content-type": "application/vnd.claimcore.recovery+json",
      "content-disposition": `attachment; filename=claimcore-recovery-${operationId}.json; filename*=UTF-8''claimcore-recovery-${operationId}.json`,
    },
  });

it("counts actual chunks at the exact boundary and rejects deceptive lengths and giant chunks", async () => {
  const { signal } = new AbortController();
  expect(
    await responseBytes(new Response("1234", { headers: { "content-length": "999" } }), 4, signal),
  ).toHaveLength(4);
  await expect(
    responseBytes(new Response("12345", { headers: { "content-length": "1" } }), 4, signal),
  ).rejects.toThrow();
  await expect(responseBytes(new Response(new Uint8Array(100_000)), 4, signal)).rejects.toThrow();
});

it("drains chunked bytes and strictly rejects invalid UTF-8 and JSON", async () => {
  const source = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(Buffer.from([0xe2]));
      controller.enqueue(Buffer.from([0x82, 0xac]));
      controller.close();
    },
  });
  const bytes = await responseBytes(
    new Response(source as unknown as BodyInit),
    3,
    new AbortController().signal,
  );
  expect(new TextDecoder().decode(bytes)).toBe("€");
  expect(() => strictJsonBytes(new Uint8Array([0xff]))).toThrow();
  expect(() => strictJsonBytes(new TextEncoder().encode("{"))).toThrow();
});

it("releases a stalled reader without awaiting hostile cancellation", async () => {
  const cancel = vi.fn(
    () =>
      new Promise<void>(() => {
        /* Hostile source never settles cancellation. */
      }),
  );
  const stream = new ReadableStream<Uint8Array>({ cancel });
  const controller = new AbortController();
  const reading = responseBytes(new Response(stream as unknown as BodyInit), 4, controller.signal);
  controller.abort();
  await expect(reading).rejects.toThrow();
  expect(cancel).toHaveBeenCalledOnce();
  expect(stream.locked).toBe(false);
  await expect(responseBytes(new Response("x"), 4, controller.signal)).rejects.toThrow();
  await expect(
    responseBytes(new Response(null), 4, new AbortController().signal),
  ).rejects.toThrow();
});

it("exports only complete matching artifact structure and preserves exact bytes", async () => {
  const fetch = vi.fn();
  vi.stubGlobal("fetch", fetch);
  const source = JSON.stringify(recoveryArtifact);
  fetch.mockResolvedValueOnce(attachment(source));
  const result = await v3.recoveryExport(operationId, "a".repeat(64), "token");
  expect(result.kind).toBe("outcome");
  if (result.kind === "outcome" && "blob" in result.value) {
    expect(await result.value.blob.text()).toBe(source);
  }
  for (const malformed of [
    {},
    { format: recoveryArtifact.format, formatVersion: 3, operationId },
    { ...recoveryArtifact, operationId: "00000000-0000-0000-0000-000000000000" },
    { ...recoveryArtifact, formatVersion: 2 },
  ]) {
    fetch.mockResolvedValueOnce(attachment(JSON.stringify(malformed)));
    expect((await v3.recoveryExport(operationId, "a".repeat(64), "token")).kind).toBe(
      "deliveryFailure",
    );
  }
});

it("observes rejected source cancellation while releasing the reader and preserving refusal", async () => {
  const stream = new ReadableStream<Uint8Array>({
    cancel: () => Promise.reject(new Error("Synthetic cancellation rejection")),
  });
  const controller = new AbortController();
  const pending = responseBytes(new Response(stream as unknown as BodyInit), 4, controller.signal);
  controller.abort();
  await expect(pending).rejects.toThrow("cancelled");
  await Promise.resolve();
  expect(stream.locked).toBe(false);
});

it("uses the full received artifact and JSON allowances independently of headers", async () => {
  const fetch = vi.fn();
  vi.stubGlobal("fetch", fetch);
  const source = JSON.stringify(recoveryArtifact);
  const exact = source.padEnd(131072, " ");
  fetch.mockResolvedValueOnce(attachment(exact));
  const accepted = await v3.recoveryExport(operationId, "a".repeat(64), "token");
  expect(accepted.kind).toBe("outcome");
  fetch.mockResolvedValueOnce(attachment(`${exact} `));
  expect((await v3.recoveryExport(operationId, "a".repeat(64), "token")).kind).toBe(
    "deliveryFailure",
  );
  const jsonLimit = 16 * 1024 * 1024;
  expect(
    await responseBytes(
      new Response("{}".padEnd(jsonLimit, " ")),
      jsonLimit,
      new AbortController().signal,
    ),
  ).toHaveLength(jsonLimit);
  await expect(
    responseBytes(
      new Response("{}".padEnd(jsonLimit + 1, " ")),
      jsonLimit,
      new AbortController().signal,
    ),
  ).rejects.toThrow();
  fetch.mockResolvedValueOnce(
    attachment(source.replace('"operationId":', '"operationId":"wrong","operationId":')),
  );
  expect((await v3.recoveryExport(operationId, "a".repeat(64), "token")).kind).toBe(
    "deliveryFailure",
  );
});
