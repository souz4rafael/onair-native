import { action } from "@elgato/streamdeck";
import { ToggleActionBase } from "./toggle-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.recording" })
export class RecordingAction extends ToggleActionBase {
	protected readonly stateField = "recording" as const;
	protected readonly command: OnAirAction = "ToggleRecording";
}
