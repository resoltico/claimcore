import { createServer } from "node:net";

// A disposable fixture must not replace a local installation's listener.
const listener = createServer();
listener.on("error", () => {
  process.stderr.write("Synthetic Web origin allocation failed.\n");
  process.exitCode = 1;
});
listener.listen(0, "127.0.0.1", () => {
  const address = listener.address();
  if (address === null || typeof address === "string") {
    listener.close();
    process.exitCode = 1;
    return;
  }
  listener.close(() => process.stdout.write(`https://localhost:${address.port}`));
});
