import {
  HttpTransportType,
  HubConnection,
  HubConnectionBuilder,
  LogLevel
} from "@microsoft/signalr";
import { DomiBaseError } from "./errors.js";

export type RealtimeOperation = "INSERT" | "UPDATE" | "DELETE";

export interface RealtimeEvent {
  table: string;
  operation: RealtimeOperation;
  rowId: string | null;
  timestamp: string;
}

export type RealtimeCallback = (event: RealtimeEvent) => void;

export interface RealtimeChannel {
  /** Subscribe to change events for this table. */
  subscribe(callback: RealtimeCallback): () => void;

  /** Unsubscribe all listeners and leave the server group. */
  unsubscribe(): Promise<void>;
}

export interface RealtimeClientOptions {
  /** How long to wait for the initial handshake before failing. */
  handshakeTimeoutMs?: number;
  /** Injectable SignalR transport; defaults to WebSockets with SSE fallback. */
  transport?: HttpTransportType;
}

interface ChannelState {
  table: string;
  callbacks: Set<RealtimeCallback>;
}

/**
 * Manages a single SignalR connection to /realtime/v1. Channels reuse the same
 * connection; the client starts on demand and stops when no channels remain.
 */
export class RealtimeClientImpl {
  private connection: HubConnection | null = null;
  private starting: Promise<void> | null = null;
  private channels = new Map<string, ChannelState>();
  private disposed = false;

  constructor(
    private readonly baseUrl: string,
    private readonly projectKey: string,
    private readonly getAccessToken: () => string | null,
    private readonly options: RealtimeClientOptions = {}
  ) {}

  channel(table: string): RealtimeChannel {
    this.ensureNotDisposed();

    if (typeof table !== "string" || table.trim().length === 0 || table.length > 63) {
      throw new DomiBaseError({ code: "INVALID_ARGUMENT", message: "Table name is invalid." });
    }

    const normalized = table.trim();

    return {
      subscribe: (callback: RealtimeCallback) => this.subscribeToTable(normalized, callback),
      unsubscribe: () => this.unsubscribeFromTable(normalized)
    };
  }

  private subscribeToTable(table: string, callback: RealtimeCallback): () => void {
    this.ensureNotDisposed();

    let state = this.channels.get(table);
    const isFirst = state === undefined;

    if (state === undefined) {
      state = { table, callbacks: new Set() };
      this.channels.set(table, state);
    }

    state.callbacks.add(callback);

    if (isFirst) {
      // Kick off server subscription in the background. Errors are delivered
      // through the connection's closed handler and logged; individual callbacks
      // do not receive a rejection here.
      this.ensureStarted()
        .then(() => this.connection?.invoke("Subscribe", table))
        .catch(() => {});
    }

    return () => {
      this.unsubscribeFromTable(table).catch(() => {});
    };
  }

  private async unsubscribeFromTable(table: string): Promise<void> {
    this.ensureNotDisposed();

    const state = this.channels.get(table);
    if (state === undefined) {
      return;
    }

    this.channels.delete(table);

    if (this.connection !== null) {
      try {
        await this.connection.invoke("Unsubscribe", table);
      } catch {
        // Best-effort; the connection may already be closing.
      }
    }

    if (this.channels.size === 0) {
      await this.stop();
    }
  }

  private async ensureStarted(): Promise<void> {
    if (this.disposed) {
      throw new DomiBaseError({ code: "REALTIME_DISPOSED", message: "Realtime client is disposed." });
    }

    if (this.connection !== null && this.connection.state === "Connected") {
      return;
    }

    if (this.starting !== null) {
      await this.starting;
      return;
    }

    this.starting = this.startInternal();

    try {
      await this.starting;
    } finally {
      this.starting = null;
    }
  }

  private async startInternal(): Promise<void> {
    const url = `${this.baseUrl.replace(/\/$/, "")}/realtime/v1`;
    const token = this.getAccessToken();

    const headers: Record<string, string> = {
      apikey: this.projectKey
    };

    if (token !== null) {
      headers.Authorization = `Bearer ${token}`;
    }

    const builder = new HubConnectionBuilder()
      .withUrl(url, {
        headers,
        transport: this.options.transport ?? HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents
      })
      .configureLogging(LogLevel.Warning);

    this.connection = builder.build();

    this.connection.on("change", (event: RealtimeEvent) => {
      this.broadcast(event);
    });

    const handshakeMs = this.options.handshakeTimeoutMs ?? 10_000;
    let timeoutId: ReturnType<typeof setTimeout> | null = null;

    const timeout = new Promise<never>((_, reject) => {
      timeoutId = setTimeout(() => {
        reject(new DomiBaseError({ code: "REALTIME_TIMEOUT", message: "Realtime handshake timed out." }));
      }, handshakeMs);
    });

    try {
      await Promise.race([this.connection.start(), timeout]);
    } finally {
      if (timeoutId !== null) {
        clearTimeout(timeoutId);
      }
    }
  }

  private broadcast(event: RealtimeEvent): void {
    const state = this.channels.get(event.table);
    if (state === undefined) {
      return;
    }

    for (const callback of state.callbacks) {
      try {
        callback(event);
      } catch {
        // User callbacks must not break other listeners.
      }
    }
  }

  private async stop(): Promise<void> {
    if (this.connection === null) {
      return;
    }

    try {
      await this.connection.stop();
    } catch {
      // Ignore close errors.
    } finally {
      this.connection = null;
    }
  }

  private ensureNotDisposed(): void {
    if (this.disposed) {
      throw new DomiBaseError({ code: "REALTIME_DISPOSED", message: "Realtime client is disposed." });
    }
  }
}
