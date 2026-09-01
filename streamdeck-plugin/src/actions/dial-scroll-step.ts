import { action } from "@elgato/streamdeck";
import { DialActionBase, type DialConfig } from "./dial-action-base.js";

@action({ UUID: "com.souz4rafael.onair.dial-scroll-step" })
export class ScrollStepDialAction extends DialActionBase {
	protected readonly config: DialConfig = {
		stateField: "scrollStep",
		increaseAction: "IncreaseScrollStep",
		decreaseAction: "DecreaseScrollStep",
		min: 20,
		max: 400,
		title: "Scroll Step",
		format: (v) => `${Math.round(v)}px`,
	};
}
