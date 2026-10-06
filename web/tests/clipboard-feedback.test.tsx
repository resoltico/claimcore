import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { CopyValue } from "../src/components/CopyValue";
import { render, screen } from "./presentation-test-support";

const clipboardAttempt = () => {
  let resolve: () => void = () => undefined;
  let reject: () => void = () => undefined;
  const promise = new Promise<void>((yes, no) => {
    resolve = yes;
    reject = () => {
      no(new Error("Synthetic clipboard refusal"));
    };
  });
  return { promise, resolve, reject };
};
const clipboard = (writeText: (value: string) => Promise<void>) => {
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } });
};

it("clears clipboard success when the value or presentation label changes", async () => {
  const user = userEvent.setup();
  clipboard(vi.fn().mockResolvedValue(undefined));
  const view = render(<CopyValue label="reference" value="CASE-1" />);
  await user.click(screen.getByRole("button"));
  expect(screen.getByText("reference copied.")).toBeVisible();
  view.rerender(<CopyValue label="reference" value="CASE-2" />);
  expect(screen.queryByText("reference copied.")).toBeNull();
  await user.click(screen.getByRole("button"));
  view.rerender(<CopyValue label="summary" value="CASE-2" />);
  expect(screen.queryByText("summary copied.")).toBeNull();
});

it("ignores late clipboard completion after the displayed value changes away and back", async () => {
  const user = userEvent.setup();
  const pending = clipboardAttempt();
  const writeText = vi.fn().mockReturnValue(pending.promise);
  clipboard(writeText);
  const view = render(<CopyValue label="reference" value="CASE-1" />);
  await user.click(screen.getByRole("button"));
  view.rerender(<CopyValue label="reference" value="CASE-2" />);
  view.rerender(<CopyValue label="reference" value="CASE-1" />);
  await act(() => {
    pending.resolve();
    return pending.promise;
  });
  expect(writeText).toHaveBeenCalledWith("CASE-1");
  expect(screen.queryByText("reference copied.")).toBeNull();
});

it("binds clipboard fallback to the latest attempted exact value", async () => {
  const user = userEvent.setup();
  const old = clipboardAttempt();
  const recent = clipboardAttempt();
  clipboard(vi.fn().mockReturnValueOnce(old.promise).mockReturnValueOnce(recent.promise));
  const view = render(<CopyValue label="reference" value="CASE-1" />);
  await user.click(screen.getByRole("button"));
  await user.click(screen.getByRole("button"));
  await act(() => {
    recent.resolve();
    return recent.promise;
  });
  await act(() => {
    old.reject();
    return old.promise.catch(() => undefined);
  });
  expect(screen.getByText("reference copied.")).toBeVisible();
  expect(screen.queryByRole("textbox")).toBeNull();
  view.rerender(<CopyValue label="reference" value={"A\u202E / 1.0000"} />);
  clipboard(vi.fn().mockRejectedValue(new Error("Synthetic refusal")));
  await user.click(screen.getByRole("button"));
  expect(screen.getByRole("textbox")).toHaveValue("A\u202E / 1.0000");
});
