import {
	SingletonAction,
	type DialAction,
	type DialRotateEvent,
	type KeyAction,
	type KeyDownEvent,
	type WillAppearEvent,
} from "@elgato/streamdeck";
import { onairClient, type OnAirAction, type RemoteState } from "../onair-client.js";

type NumericStateField = "opacity" | "fontSize" | "scrollSpeed" | "voiceScrollSpeed" | "scrollStep" | "voiceThreshold";

export interface DialConfig {
	readonly stateField: NumericStateField;
	readonly increaseAction: OnAirAction;
	readonly decreaseAction: OnAirAction;
	readonly min: number;
	readonly max: number;
	readonly title: string;
	/** Formats the raw value for the touch-strip "value" text and the Keypad-fallback title. */
	readonly format: (value: number) => string;
}

/**
 * Shared base for the 6 Stream Deck+ dial actions (opacity, font size, auto-scroll speed, voice
 * scroll speed, manual scroll step, voice scroll sensitivity). All 6 already have Ctrl+Alt+
 * global hotkey pairs in onAIr for the same increase/decrease step — rotating the dial just
 * fires that same pair of commands, once per tick, so the physical dial and the keyboard
 * shortcut can never drift out of sync with each other's step size.
 *
 * Also usable as a plain Keypad action (Controllers: ["Keypad", "Encoder"] in the manifest) for
 * users without a Stream Deck+: a press nudges the value up by one step, same as a single dial
 * tick.
 */
export abstract class DialActionBase extends SingletonAction {
	protected abstract readonly config: DialConfig;

	private subscribed = false;

	override onWillAppear(ev: WillAppearEvent): void | Promise<void> {
		if (!this.subscribed) {
			this.subscribed = true;
			onairClient.onState((state) => this.syncAll(state));
		}
		const state = onairClient.currentState;
		if (state) return this.applyState(ev.action, state);
	}

	override onDialRotate(ev: DialRotateEvent): void | Promise<void> {
		const ticks = ev.payload.ticks;
		const step = ticks > 0 ? this.config.increaseAction : this.config.decreaseAction;
		for (let i = 0; i < Math.abs(ticks); i++) onairClient.send(step);
	}

	override onKeyDown(_ev: KeyDownEvent): void {
		onairClient.send(this.config.increaseAction);
	}

	private syncAll(state: RemoteState): void {
		for (const a of this.actions) this.applyState(a, state);
	}

	private applyState(a: DialAction | KeyAction, state: RemoteState): void {
		const { config } = this;
		const value = state[config.stateField];
		const pct = Math.round(((value - config.min) / (config.max - config.min)) * 100);
		const clampedPct = Math.max(0, Math.min(100, pct));
		const text = config.format(value);

		if (a.isDial()) {
			void a.setFeedback({ title: config.title, value: text, bar: clampedPct });
		} else if (a.isKey()) {
			void a.setTitle(`${config.title}\n${text}`);
		}
	}
}
