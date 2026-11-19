import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { DomiBaseError, createClient } from "../dist/index.js";
import { BASE_URL, PUBLIC_KEY, createRecorder, errorResponse, jsonResponse } from "./helpers.js";

const SESSION = {
  accessToken: "eyJhbGciOiJFUzI1NiJ9.test",
  refreshToken: "refresh-token-42",
  expiresAt: "2026-11-17T20:00:00.000Z",
  user: {
    id: "550e8400-e29b-41d4-a716-446655440000",
    email: "user@example.com",
    createdAt: "2026-11-17T00:00:00.000Z"
  }
};

function createAuthClient(handler) {
  const recorder = createRecorder(handler);
  const client = createClient({
    url: BASE_URL,
    projectKey: PUBLIC_KEY,
    options: { fetch: recorder.fetchImpl }
  });
  return { client, calls: recorder.calls };
}

function authResponse(session) {
  return jsonResponse({ data: session });
}

describe("auth client", () => {
  it("signs up and stores the session", async () => {
    const { client, calls } = createAuthClient(() => authResponse(SESSION));

    const result = await client.auth.signUp({ email: "user@example.com", password: "secret123" });

    assert.equal(result.error, null);
    assert.deepEqual(result.data, SESSION);
    assert.equal(client.auth.getSession().accessToken, SESSION.accessToken);

    const call = calls[0];
    assert.equal(call.url, `${BASE_URL}/auth/v1/register`);
    assert.equal(call.method, "POST");
    assert.equal(call.headers["apikey"], PUBLIC_KEY);
    assert.deepEqual(JSON.parse(call.body), { email: "user@example.com", password: "secret123" });
  });

  it("signs in with password and stores the session", async () => {
    const { client, calls } = createAuthClient(() => authResponse(SESSION));

    const result = await client.auth.signInWithPassword({ email: "user@example.com", password: "secret123" });

    assert.equal(result.error, null);
    assert.deepEqual(result.data, SESSION);

    const call = calls[0];
    assert.equal(call.url, `${BASE_URL}/auth/v1/login`);
    assert.equal(call.method, "POST");
  });

  it("refreshes the session", async () => {
    const refreshed = { ...SESSION, accessToken: "new-access-token", refreshToken: "new-refresh-token" };
    const { client, calls } = createAuthClient(() => authResponse(refreshed));

    const result = await client.auth.refreshSession("old-refresh-token");

    assert.equal(result.error, null);
    assert.equal(result.data.accessToken, "new-access-token");
    assert.equal(client.auth.getSession().accessToken, "new-access-token");

    const call = calls[0];
    assert.equal(call.url, `${BASE_URL}/auth/v1/refresh`);
    assert.deepEqual(JSON.parse(call.body), { refreshToken: "old-refresh-token" });
  });

  it("signs out and revokes the refresh token", async () => {
    const { client, calls } = createAuthClient((call) => {
      if (call.url.includes("/auth/v1/logout")) {
        return jsonResponse({ data: null });
      }
      return authResponse(SESSION);
    });
    await client.auth.signUp({ email: "user@example.com", password: "secret123" });

    const result = await client.auth.signOut();

    assert.equal(result.error, null);
    assert.equal(client.auth.getSession(), null);

    const call = calls[1];
    assert.equal(call.url, `${BASE_URL}/auth/v1/logout`);
    assert.deepEqual(JSON.parse(call.body), { refreshToken: SESSION.refreshToken });
  });

  it("notifies auth state listeners", async () => {
    const { client } = createAuthClient(() => authResponse(SESSION));
    const events = [];
    const unsubscribe = client.auth.onAuthStateChange((event, session) => {
      events.push({ event, accessToken: session?.accessToken ?? null });
    });

    await client.auth.signUp({ email: "user@example.com", password: "secret123" });
    await client.auth.signOut();
    unsubscribe();

    assert.deepEqual(events, [
      { event: "SIGNED_IN", accessToken: SESSION.accessToken },
      { event: "SIGNED_OUT", accessToken: null }
    ]);
  });

  it("returns server errors without storing a session", async () => {
    const { client } = createAuthClient(() =>
      errorResponse(401, "INVALID_CREDENTIALS", "Invalid email or password.")
    );

    const result = await client.auth.signInWithPassword({ email: "user@example.com", password: "wrong" });

    assert.equal(result.data, null);
    assert.ok(result.error instanceof DomiBaseError);
    assert.equal(result.error.code, "INVALID_CREDENTIALS");
    assert.equal(client.auth.getSession(), null);
  });

  it("sends the access token on runtime requests after sign in", async () => {
    const { client, calls } = createAuthClient((call) => {
      if (call.url.includes("/auth/v1/")) {
        return authResponse(SESSION);
      }
      return jsonResponse({ data: [{ id: 1 }], count: 1 });
    });

    await client.auth.signUp({ email: "user@example.com", password: "secret123" });
    await client.from("contactos").select("id");

    const runtimeCall = calls[1];
    assert.equal(runtimeCall.headers["Authorization"], `Bearer ${SESSION.accessToken}`);
  });
});
