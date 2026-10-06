import https from "node:https";

/** @param {number} port @param {Uint8Array} ca @param {string} path @param {string} [host] @param {string} [servername]
 * @returns {Promise<{status: number | undefined, headers: import("node:http").IncomingHttpHeaders}>}
 */
export function probe(port, ca, path, host = "app.localhost:5443", servername = "app.localhost") {
  return new Promise((resolve, reject) => {
    const request = https.request(
      {
        hostname: "127.0.0.1",
        port,
        servername,
        ca: Buffer.from(ca),
        path,
        method: "GET",
        headers: { Host: host },
        timeout: 5000,
      },
      (response) => {
        response.resume();
        response.on("end", () =>
          resolve({ status: response.statusCode, headers: response.headers }),
        );
      },
    );
    request.on("error", reject);
    request.on("timeout", () => request.destroy(new Error("HTTPS qualification deadline")));
    request.end();
  });
}
