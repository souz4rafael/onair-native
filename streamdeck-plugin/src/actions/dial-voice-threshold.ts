import { action } from "@elgato/streamdeck";
import { DialActionBase, type DialConfig } from "./dial-action-base.js";

@action({ UUID: "com.souz4rafael.onair.dial-voice-threshold" })
export class VoiceThresholdDialAction extends DialActionBase {
	protected readonly config: DialConfig = {
		stateField: "voiceThreshold",
		increaseAction: "IncreaseVoiceThreshold",
		decreaseAction: "DecreaseVoiceThreshold",
		min: 1,
		max: 50,
		title: "Sensitivity",
		format: (v) => v.toFixed(1),
	};
}
