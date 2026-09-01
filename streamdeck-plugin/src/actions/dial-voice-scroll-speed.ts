import { action } from "@elgato/streamdeck";
import { DialActionBase, type DialConfig } from "./dial-action-base.js";

@action({ UUID: "com.souz4rafael.onair.dial-voice-scroll-speed" })
export class VoiceScrollSpeedDialAction extends DialActionBase {
	protected readonly config: DialConfig = {
		stateField: "voiceScrollSpeed",
		increaseAction: "IncreaseVoiceScrollSpeed",
		decreaseAction: "DecreaseVoiceScrollSpeed",
		min: 1,
		max: 100,
		title: "Voice Speed",
		format: (v) => `${Math.round(v)}`,
	};
}
