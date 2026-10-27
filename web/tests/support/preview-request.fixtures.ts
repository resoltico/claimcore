/** Provides the reviewed values for the exact synthetic request sent by the editor. */
export const previewForRequest =
  (reply: Response) =>
  (_input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    if (typeof init?.body !== "string") {
      throw new Error("Expected an exact serialized draft.");
    }
    return Promise.resolve(reply);
  };
