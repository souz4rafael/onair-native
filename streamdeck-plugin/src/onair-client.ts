import streamDeck from "@elgato/streamdeck";
import WebSocket from "ws";

/** Mirrors OnAirNative.Services.RemoteState — keep both in sync when adding fields. */
export interface RemoteState {
	tpOpen: boolean;
	tpLocked: boolean;
	tpHiddenInShare: boolean;
	controllerHiddenInShare: boolean;
	recording: boolean;
	chatProvider: string;
	whisperLocalLoaded: boolean;
	whisperModelStatus: string;
	opacity: number;
	fontSize: number;
	scrollSpeed: number;
	voiceScrollSpeed: number;
	scrollStep: number;
	voiceThreshold: number;
	/** "Manual" | "Auto" | "Voice" */
	scrollMode: string;
	fontFamily: string;
	loadedScriptName: string;

	// ── Q&A monitoring + Copilot insights (Block 6) ───────────────────────────
	lastQuestion: string;
	lastAnswer: string;
	qaTurnCount: number;
	pacingSummary: string;
	/** "None" | "Slow" | "Good" | "Fast" */
	pacingLevel: string;
	followUpSuggestions: string[];
	qaSessionActive: boolean;
	insightText: string;

	// ── AI Insights window (separate resizable Controller-tab-driven window) ─
	insightsOpen: boolean;
	insightsLocked: boolean;
	insightsHiddenInShare: boolean;
	insightFontSize: number;
	insightOpacity: number;
	insightFontFamily: string;
}

/** Mirrors OnAirNative.Services.HotkeyAction — the shared command vocabulary between the
 * physical global hotkeys and this plugin. */
export type OnAirAction =
	| "ScrollUp"
	| "ScrollDown"
	| "ToggleMoveMode"
	| "OpenFile"
	| "ToggleRecording"
	| "IncreaseOpacity"
	| "DecreaseOpacity"
	| "ReleaseStealthContainer"
	| "ToggleOverlayVisibility"
	| "ToggleOverlayCaptureProtection"
	| "ToggleControllerCaptureProtection"
	| "IncreaseScrollSpeed"
	| "DecreaseScrollSpeed"
	| "IncreaseFontSize"
	| "DecreaseFontSize"
	| "IncreaseVoiceScrollSpeed"
	| "DecreaseVoiceScrollSpeed"
	| "IncreaseScrollStep"
	| "DecreaseScrollStep"
	| "IncreaseVoiceThreshold"
	| "DecreaseVoiceThreshold"
	| "RecheckWhisperModel"
	| "ToggleInsightsVisibility"
	| "ToggleInsightsLock"
	| "ToggleInsightsCaptureProtection";

const PORT = 47823;
const RECONNECT_DELAY_MS = 2000;

type StateListener = (state: RemoteState) => void;
type ConnectionListener = (connected: boolean) => void;

/**
 * Singleton WebSocket client connecting to onAIr's RemoteControlService (loopback-only,
 * 127.0.0.1:47823). Handles reconnection with a fixed delay whenever onAIr isn't running or
 * restarts — every action in this plugin subscribes to the same shared connection rather than
 * opening one socket each, since Stream Deck instantiates a fresh action object per visible key
 * but they all want the same live state.
 */
class OnAirClient {
	private ws: WebSocket | null = null;
	private readonly stateListeners = new Set<StateListener>();
	private readonly connectionListeners = new Set<ConnectionListener>();
	private lastState: RemoteState | null = null;
	private connected = false;
	private reconnectTimer: ReturnType<typeof setTimeout> | null = null;

	connect(): void {
		if (this.ws) return;

		let socket: WebSocket;
		try {
			socket = new WebSocket(`ws://127.0.0.1:${PORT}/`);
		} catch (err) {
			streamDeck.logger.warn(`onAIr: WebSocket construction failed: ${err}`);
			this.scheduleReconnect();
			return;
		}
		this.ws = socket;

		socket.addEventListener("open", () => {
			this.connected = true;
			this.connectionListeners.forEach((l) => l(true));
			streamDeck.logger.info("onAIr: connected");
		});

		socket.addEventListener("message", (ev) => {
			try {
				const msg = JSON.parse(String(ev.data));
				if (msg?.op === "state" && msg.data) {
					this.lastState = msg.data as RemoteState;
					this.stateListeners.forEach((l) => l(this.lastState as RemoteState));
				}
			} catch (err) {
				streamDeck.logger.warn(`onAIr: failed to parse message: ${err}`);
			}
		});

		const onDown = () => {
			const wasConnected = this.connected;
			this.connected = false;
			this.ws = null;
			if (wasConnected) {
				streamDeck.logger.info("onAIr: disconnected, will retry");
				this.connectionListeners.forEach((l) => l(false));
			}
			this.scheduleReconnect();
		};
		socket.addEventListener("close", onDown);
		socket.addEventListener("error", onDown);
	}

	private scheduleReconnect(): void {
		if (this.reconnectTimer) return;
		this.reconnectTimer = setTimeout(() => {
			this.reconnectTimer = null;
			this.connect();
		}, RECONNECT_DELAY_MS);
	}

	/** Fires a toggle/one-shot command (e.g. ToggleOverlayVisibility) or a relative dial/step
	 * adjustment (e.g. IncreaseOpacity) — onAIr's RemoteControlService treats both ops
	 * identically, both just resolve to a HotkeyAction and execute it. */
	send(action: OnAirAction): void {
		if (!this.ws || this.ws.readyState !== WebSocket.OPEN) return;
		try {
			this.ws.send(JSON.stringify({ op: "command", action }));
		} catch (err) {
			streamDeck.logger.warn(`onAIr: send failed: ${err}`);
		}
	}

	/** Registers a listener for state pushes; immediately replays the last known state (if any)
	 * so a newly-created action instance doesn't have to wait for the next broadcast. Returns an
	 * unsubscribe function. */
	onState(listener: StateListener): () => void {
		this.stateListeners.add(listener);
		if (this.lastState) listener(this.lastState);
		return () => this.stateListeners.delete(listener);
	}

	/** Registers a listener for connect/disconnect transitions; immediately replays the current
	 * status. Returns an unsubscribe function. */
	onConnectionChange(listener: ConnectionListener): () => void {
		this.connectionListeners.add(listener);
		listener(this.connected);
		return () => this.connectionListeners.delete(listener);
	}

	get isConnected(): boolean {
		return this.connected;
	}

	get currentState(): RemoteState | null {
		return this.lastState;
	}
}

export const onairClient = new OnAirClient();
