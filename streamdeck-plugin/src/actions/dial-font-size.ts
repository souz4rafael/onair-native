import { action } from "@elgato/streamdeck";
import { DialActionBase, type DialConfig } from "./dial-action-base.js";

@action({ UUID: "com.souz4rafael.onair.dial-font-size" })
export class FontSizeDialAction extends DialActionBase {
	protected readonly config: DialConfig = {
		stateField: "fontSize",
		increaseAction: "IncreaseFontSize",
		decreaseAction: "DecreaseFontSize",
		min: 10,
		max: 64,
		title: "Font Size",
		format: (v) => `${Math.round(v)}px`,
	};
}
