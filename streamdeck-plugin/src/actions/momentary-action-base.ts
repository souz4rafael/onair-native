import { SingletonAction, type KeyDownEvent } from "@elgato/streamdeck";
import { onairClient, type OnAirAction } from "../onair-client.js";

/**
 * Shared base for one-shot "trigger" actions (Release App Stealth, Open File, Scroll Up,
 * Scroll Down) — unlike the toggle actions, these have no on/off state to track or reflect;
 * a press just fires the command once. Shows Stream Deck's built-in transient "OK" checkmark
 * as the only feedback, since there's no persistent state to display.
 */
export abstract class MomentaryActionBase extends SingletonAction {
	protected abstract readonly command: OnAirAction;

	override onKeyDown(ev: KeyDownEvent): void {
		onairClient.send(this.command);
		if (ev.action.isKey()) void ev.action.showOk();
	}
}
