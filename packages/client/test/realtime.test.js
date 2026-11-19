import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { DomiBaseError, createClient } from "../dist/index.js";
import { BASE_URL, PUBLIC_KEY } from "./helpers.js";

describe("realtime client", () => {
  it("exposes a channel factory", () => {
    const client = createClient({ url: BASE_URL, projectKey: PUBLIC_KEY });
    const channel = client.realtime.channel("eventos");

    assert.equal(typeof channel.subscribe, "function");
    assert.equal(typeof channel.unsubscribe, "function");
  });

  it("rejects invalid table names", () => {
    const client = createClient({ url: BASE_URL, projectKey: PUBLIC_KEY });

    assert.throws(() => client.realtime.channel(""), (error) => {
      assert.ok(error instanceof DomiBaseError);
      assert.equal(error.code, "INVALID_ARGUMENT");
      return true;
    });

    assert.throws(() => client.realtime.channel("a".repeat(64)), (error) => {
      assert.ok(error instanceof DomiBaseError);
      assert.equal(error.code, "INVALID_ARGUMENT");
      return true;
    });
  });


});
