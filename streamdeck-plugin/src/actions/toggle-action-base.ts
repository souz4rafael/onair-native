import { SingletonAction, type KeyDownEvent, type WillAppearEvent } from "@elgato/streamdeck";
import { onairClient, type OnAirAction, type RemoteState } from "../onair-client.js";

type BoolStateField = "tpOpen" | "tpLocked" | "tpHiddenInShare" | "controllerHiddenInShare" | "recording";

/**
 * Shared base for the 5 plain toggle actions (Open/Hide TP, Lock/Unlock TP, Hide TP in Share,
 * Hide Controller in Share, Start/Stop Recording): a single press sends one command, and the
 * key's 2-state icon (defined in the manifest) reflects the corresponding boolean field of the
 * last known {@link RemoteState}, kept in sync for every currently-visible instance of the
 * action across all connected Stream Deck devices/profiles.
 *
 * Subscribing to onairClient in onWillAppear (not the constructor) is deliberate: it guarantees
 * the subclass's `stateField`/`command` field initializers have already run by the time the
 * subscription's callback can possibly fire, avoiding a use-before-init race that a
 * constructor-time subscription could hit if a state snapshot is already cached.
 *
 * `setState()` is only called when the value actually changes (tracked per action instance via
 * `lastApplied`) rather than unconditionally on every state push (which arrives at least every
 * 2s from onAIr's safety-net timer, regardless of whether anything changed). Calling `setState`
 * redundantly with the same value was observed to reset the Stream Deck app's own per-key
 * "hide title" override back to visible — the state-1 titles (Unlock TP / Show TP / Show
 * Controller) kept reappearing right after a user turned them off in the property inspector.
 */
export abstract class ToggleActionBase extends SingletonAction {
	protected abstract readonly stateField: BoolStateField;
	protected abstract readonly command: OnAirAction;

	private subscribed = false;
	private readonly lastApplied = new Map<string, 0 | 1>();

	override onWillAppear(ev: WillAppearEvent): void | Promise<void> {
		if (!this.subscribed) {
			this.subscribed = true;
			onairClient.onState((state) => this.syncAll(state));
		}
		const state = onairClient.currentState;
		if (state && ev.action.isKey()) {
			return this.applyIfChanged(ev.action.id, ev.action.setState.bind(ev.action), state[this.stateField] ? 1 : 0);
		}
	}

	override onKeyDown(_ev: KeyDownEvent): void {
		onairClient.send(this.command);
	}

	private syncAll(state: RemoteState): void {
		for (const a of this.actions) {
			if (a.isKey()) this.applyIfChanged(a.id, a.setState.bind(a), state[this.stateField] ? 1 : 0);
		}
	}

	private applyIfChanged(id: string, setState: (value: 0 | 1) => Promise<void>, value: 0 | 1): void {
		if (this.lastApplied.get(id) === value) return;
		this.lastApplied.set(id, value);
		void setState(value);
	}
}
