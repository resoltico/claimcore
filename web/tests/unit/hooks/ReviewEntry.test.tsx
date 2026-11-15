import { act, fireEvent } from "@testing-library/react";
import { expect, it, vi } from "vitest";
import { render, screen, testWorkspaceIdentity } from "../../support/presentation-test-support";
import { useReviewEntry } from "../../../src/views/operation/useReviewEntry";
import type { EffectIdentity } from "../../../src/operation/operationTypes";
const first: EffectIdentity = {
  owner: testWorkspaceIdentity,
  credential: "token",
  contextGeneration: 1,
  id: "review-one",
  target: "case-one",
};
const Fixture = ({
  identity,
  visible = true,
  portal = true,
}: {
  identity: EffectIdentity;
  visible?: boolean;
  portal?: boolean;
}) => {
  const { headingRef, bodyRef } = useReviewEntry(identity, visible, false);
  return portal ? (
    <>
      <h2 ref={headingRef} tabIndex={-1}>
        Review beginning
      </h2>
      <div
        ref={(node) => {
          bodyRef(node);
          if (node) {
            node.scrollTop = 900;
          }
        }}
        data-testid="review-body"
      >
        <input aria-label="Deliberate confirmation" />
      </div>
    </>
  ) : null;
};
it("enters each visible review once, defers hidden arrivals and preserves a portal detour", () => {
  const frames = new Map<number, FrameRequestCallback>();
  let sequence = 0;
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    frames.set(++sequence, callback);
    return sequence;
  });
  vi.stubGlobal("cancelAnimationFrame", (key: number) => {
    frames.delete(key);
  });
  const flush = () => {
    act(() => {
      for (const [key, callback] of [...frames]) {
        frames.delete(key);
        callback(0);
      }
    });
  };
  try {
    const view = render(<Fixture identity={first} visible={false} />);
    flush();
    expect(screen.getByTestId("review-body").scrollTop).toBe(900);
    view.rerender(<Fixture identity={first} />);
    flush();
    expect(screen.getByTestId("review-body").scrollTop).toBe(0);
    expect(screen.getByRole("heading")).toHaveFocus();
    const body = screen.getByTestId("review-body");
    body.scrollTop = 420;
    fireEvent.scroll(body);
    view.rerender(<Fixture identity={{ ...first }} />);
    flush();
    expect(screen.getByTestId("review-body").scrollTop).toBe(420);
    view.rerender(<Fixture identity={first} portal={false} />);
    flush();
    view.rerender(<Fixture identity={first} />);
    flush();
    expect(screen.getByTestId("review-body").scrollTop).toBe(420);
    view.rerender(<Fixture identity={{ ...first, id: "hidden-stale" }} visible={false} />);
    view.rerender(<Fixture identity={{ ...first, id: "current-preview" }} />);
    flush();
    expect(screen.getByTestId("review-body").scrollTop).toBe(0);
    expect(screen.getByRole("heading")).toHaveFocus();
  } finally {
    vi.unstubAllGlobals();
  }
});

it("does not steal deliberate confirmation input before the deferred heading frame", () => {
  let callback: FrameRequestCallback | null = null;
  vi.stubGlobal("requestAnimationFrame", (next: FrameRequestCallback) => {
    callback = next;
    return 1;
  });
  vi.stubGlobal("cancelAnimationFrame", () => {
    callback = null;
  });
  try {
    render(<Fixture identity={first} />);
    const input = screen.getByRole("textbox", { name: "Deliberate confirmation" });
    fireEvent.pointerDown(input);
    input.focus();
    fireEvent.change(input, { target: { value: "CASE-EXACT" } });
    const pending: FrameRequestCallback | null = callback;
    if (pending !== null) {
      act(() => {
        (pending as FrameRequestCallback)(0);
      });
    }
    expect(input).toHaveFocus();
    expect(input).toHaveValue("CASE-EXACT");
  } finally {
    vi.unstubAllGlobals();
  }
});
