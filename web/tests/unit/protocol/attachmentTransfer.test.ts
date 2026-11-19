import { expect, test, vi, afterEach } from "vitest";
import { requestAttachment } from "../../../src/api/attachments";
import type { OwnerScope } from "../../../src/protocol/ownerScope";

const installation = "3a793959-acb6-4eba-905f-2426a4904637";
const owner: OwnerScope = {
  serviceOrigin: window.location.origin,
  installationId: installation,
  actorId: "80000000-0000-4000-8000-000000000001",
  sessionEpoch: 2,
};
const selector = {
  target: { kind: "ID", caseId: "90000000-0000-4000-8000-000000000001" },
  attachmentId: "a0000000-0000-4000-8000-000000000001",
} as const;
const independentlyHashed = Uint8Array.from([
  186, 120, 22, 191, 143, 1, 207, 234, 65, 65, 64, 222, 93, 174, 34, 35, 176, 3, 97, 163, 150, 23,
  122, 156, 180, 16, 255, 97, 242, 0, 21, 173,
]);
const response = (headers: Readonly<Record<string, string>> = {}, body = "abc") =>
  new Response(body, {
    headers: {
      "content-type": "application/octet-stream",
      "content-length": "3",
      "content-encoding": "identity",
      "content-disposition": "attachment; filename=\"evidence.bin\"; filename*=UTF-8''evidence.bin",
      "ClaimCore-Installation": installation,
      "cache-control": "no-store, no-transform",
      "Content-Digest": "sha-256=:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=:",
      ...headers,
    },
  });
const request = (currentOwner: () => OwnerScope | null = () => owner) =>
  requestAttachment(
    "download",
    selector,
    "evidence.bin",
    "cookie-token",
    owner,
    currentOwner,
    new AbortController().signal,
  );
afterEach(() => {
  vi.unstubAllGlobals();
});

test("requires exact verified binary success before releasing download bytes", async () => {
  const digest = vi.fn(() => Promise.resolve(independentlyHashed.buffer));
  vi.stubGlobal("crypto", { subtle: { digest } });
  const send = vi.fn(() => Promise.resolve(response()));
  vi.stubGlobal("fetch", send);
  await expect(request()).resolves.toMatchObject({
    kind: "outcome",
    value: {
      kind: "RECEIVED",
      file: { bytes: Uint8Array.of(97, 98, 99), mediaType: "application/octet-stream" },
    },
  });
  expect(send).toHaveBeenCalledWith(
    "/api/attachment.download",
    expect.objectContaining({
      redirect: "error",
      method: "POST",
      body: JSON.stringify(selector),
    }),
  );
  expect(digest).toHaveBeenCalledTimes(1);
});
test.each([
  ["wrong owner", { "ClaimCore-Installation": "wrong" }, "abc"],
  ["malformed digest", { "Content-Digest": "sha-256=:not-an-hash:" }, "abc"],
  [
    "wrong digest",
    { "Content-Digest": "sha-256=:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=:" },
    "abc",
  ],
  ["truncation", {}, "ab"],
  ["long result", {}, "abcd"],
  ["unexpected encoding", { "Content-Encoding": "gzip" }, "abc"],
] as const)("rejects %s without delivering a document", async (_label, headers, body) => {
  vi.stubGlobal("crypto", {
    subtle: { digest: () => Promise.resolve(independentlyHashed.buffer) },
  });
  vi.stubGlobal("fetch", () => Promise.resolve(response(headers, body)));
  await expect(request()).resolves.toMatchObject({ kind: "deliveryFailure" });
});
test("drops data after hashing if its owner changes", async () => {
  const hash = Promise.withResolvers<ArrayBuffer>();
  const digest = vi.fn(() => hash.promise);
  vi.stubGlobal("crypto", { subtle: { digest } });
  vi.stubGlobal("fetch", () => Promise.resolve(response()));
  let current: OwnerScope | null = owner;
  const read = request(() => current);
  await vi.waitFor(() => {
    expect(digest).toHaveBeenCalledTimes(1);
  });
  current = null;
  hash.resolve(independentlyHashed.buffer);
  await expect(read).resolves.toMatchObject({
    kind: "deliveryFailure",
    notice: { reason: "cancelled" },
  });
});

test("cancels a response body with a malformed extended filename", async () => {
  const malformed = response({
    "content-disposition": "attachment; filename=\"evidence.bin\"; filename*=UTF-8''bad%ZZ",
  });
  const cancel = vi.spyOn(malformed.body!, "cancel");
  vi.stubGlobal("fetch", () => Promise.resolve(malformed));
  await expect(request()).resolves.toMatchObject({ kind: "deliveryFailure" });
  expect(cancel).toHaveBeenCalledTimes(1);
});
