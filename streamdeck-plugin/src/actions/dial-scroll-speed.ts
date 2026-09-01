import { action } from "@elgato/streamdeck";
import { DialActionBase, type DialConfig } from "./dial-action-base.js";

@action({ UUID: "com.souz4rafael.onair.dial-scroll-speed" })
export class ScrollSpeedDialAction extends DialActionBase {
	protected readonly config: DialConfig = {
		stateField: "scrollSpeed",
		increaseAction: "IncreaseScrollSpeed",
		decreaseAction: "DecreaseScrollSpeed",
		min: 1,
		max: 100,
		title: "Auto Speed",
		format: (v) => `${Math.round(v)}`,
	};
}
