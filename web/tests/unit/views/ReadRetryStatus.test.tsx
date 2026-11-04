import { act } from "@testing-library/react";
import { expect, it } from "vitest";
import { render, screen, TestSession } from "../../support/presentation-test-support";
import { captureReadRetryNotice } from "../../../src/api/readRetryNotice";
import { ReadRetryStatus } from "../../../src/views/ReadRetryStatus";
it("announces current read waits without opening a dialog and clears on a session replacement", () => {
  const view = render(
    <TestSession credential="first">
      <ReadRetryStatus />
    </TestSession>,
  );
  const begin = captureReadRetryNotice("first");
  let end = () => {
    /* Assigned by the actual current dispatch. */
  };
  act(() => {
    end = begin();
  });
  expect(screen.getByRole("status")).toHaveTextContent("Busy — retrying");
  expect(screen.queryByRole("dialog")).toBeNull();
  view.rerender(
    <TestSession credential="second">
      <ReadRetryStatus />
    </TestSession>,
  );
  expect(screen.queryByRole("status")).toBeNull();
  act(() => {
    end();
    begin()();
  });
  expect(screen.queryByRole("status")).toBeNull();
});
