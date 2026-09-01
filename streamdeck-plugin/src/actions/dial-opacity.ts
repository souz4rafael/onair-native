import { action } from "@elgato/streamdeck";
import { DialActionBase, type DialConfig } from "./dial-action-base.js";

@action({ UUID: "com.souz4rafael.onair.dial-opacity" })
export class OpacityDialAction extends DialActionBase {
	protected readonly config: DialConfig = {
		stateField: "opacity",
		increaseAction: "IncreaseOpacity",
		decreaseAction: "DecreaseOpacity",
		min: 10,
		max: 100,
		title: "Opacity",
		format: (v) => `${Math.round(v)}%`,
	};
}
