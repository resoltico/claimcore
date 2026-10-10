import type { Page } from "@playwright/test";
import { webV3TransportLimits } from "../src/generated/contracts/web-v3.endpoint-catalog";

const configured = new WeakMap<Page, Promise<void>>();

const configure = async (page: Page): Promise<void> => {
  if (page.context().browser()?.browserType().name() !== "chromium") {
    return;
  }
  // The native page/context lifetime closes this session and its retained bodies.
  const session = await page.context().newCDPSession(page);
  await session.send("Network.configureDurableMessages", {
    maxTotalBufferSize: 2 * webV3TransportLimits.jsonResponseBytes,
    maxResourceBufferSize: webV3TransportLimits.jsonResponseBytes,
  });
};

/** Preserve the original streamed reply for the existing native HTTP-body assertions. */
export const retainResponseBodies = (page: Page): Promise<void> => {
  const existing = configured.get(page);
  if (existing !== undefined) {
    return existing;
  }
  const pending = configure(page);
  configured.set(page, pending);
  return pending;
};
