import { act, render, screen } from "@testing-library/react";
import { useCallback, useEffect, useSyncExternalStore } from "react";
import { afterEach, expect, it, vi } from "vitest";
import {
  EndpointProvider,
  useDisplayGeneration,
  useEndpointCache,
} from "../../../src/hooks/EndpointProvider";
import { humanReadReply } from "../../support/human-read-reply.fixtures";
import { activeRequester } from "../../support/human-requester.fixtures";

const read = vi.fn(() =>
  Promise.resolve({
    kind: "outcome" as const,
    status: 200,
    value: humanReadReply("context", activeRequester),
  }),
);

const Fixture = () => {
  const cache = useEndpointCache()!;
  const key = `probe-${useDisplayGeneration()}`;
  const snapshot = useSyncExternalStore(
    useCallback((listener: () => void) => cache.subscribe(key, listener), [cache, key]),
    () => cache.snapshot(key),
  );
  useEffect(() => {
    void cache.fetch(key, read);
  }, [cache, key]);
  return <div>{snapshot.result?.kind === "outcome" ? "PRIVATE_RESULT" : "masked"}</div>;
};

afterEach(() => {
  vi.restoreAllMocks();
  read.mockClear();
});

it("admits the visible online control", async () => {
  vi.spyOn(document, "visibilityState", "get").mockReturnValue("visible");
  vi.spyOn(navigator, "onLine", "get").mockReturnValue(true);
  render(
    <EndpointProvider>
      <Fixture />
    </EndpointProvider>,
  );
  expect(await screen.findByText("PRIVATE_RESULT")).toBeInTheDocument();
  expect(read).toHaveBeenCalledOnce();
});

it.each([
  ["hidden", true],
  ["visible", false],
] as const)(
  "waits for availability and a new request when initially %s and online=%s",
  async (visibility, online) => {
    const visible = vi.spyOn(document, "visibilityState", "get").mockReturnValue(visibility);
    const connected = vi.spyOn(navigator, "onLine", "get").mockReturnValue(online);
    render(
      <EndpointProvider>
        <Fixture />
      </EndpointProvider>,
    );
    expect(screen.getByText("masked")).toBeInTheDocument();
    expect(read).not.toHaveBeenCalled();

    visible.mockReturnValue("visible");
    connected.mockReturnValue(true);
    act(() => {
      window.dispatchEvent(new Event("online"));
    });
    expect(await screen.findByText("PRIVATE_RESULT")).toBeInTheDocument();
    expect(read).toHaveBeenCalledOnce();
  },
);
