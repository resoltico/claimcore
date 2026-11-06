import { useLayoutEffect } from "react";
import { useEndpointCache } from "../../src/hooks/EndpointProvider";
import userEvent from "@testing-library/user-event";
import { screen, waitFor, within } from "@testing-library/react";

/** Enter through the offered People action; no operation editor exists before this consent. */
export const openPersonEditor = async () => {
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Add person" }));
  return screen.findByRole("dialog", { name: "Add person" });
};

export const chooseAdmissionRole = async () => {
  const user = userEvent.setup();
  await user.click(screen.getByRole("radio", { name: "Case reader" }));
};

/** The person drawer has both a header and footer Close action. */
export const closePersonDetails = async () => {
  const user = userEvent.setup();
  const drawer = screen.getByRole("dialog");
  const close = within(drawer).getAllByRole("button", { name: "Close" })[0]!;
  close.focus();
  await user.click(close);
  await waitFor(() => {
    if (screen.queryByRole("dialog") !== null) {
      throw new Error("Person details must close before returning to the directory.");
    }
  });
};

/** Exercise external cache invalidation without clicking the inert directory behind a drawer. */
export const DisclosureRefreshDriver = ({
  capture,
}: {
  capture: (invalidate: () => void) => void;
}) => {
  const cache = useEndpointCache();
  useLayoutEffect(() => {
    if (cache === null) {
      throw new Error("Disclosure refresh requires the real endpoint provider.");
    }
    capture(cache.invalidate);
  }, [cache, capture]);
  return null;
};
