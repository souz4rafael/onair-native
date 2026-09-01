import { action } from "@elgato/streamdeck";
import { ToggleActionBase } from "./toggle-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.lock-tp" })
export class LockTpAction extends ToggleActionBase {
	protected readonly stateField = "tpLocked" as const;
	protected readonly command: OnAirAction = "ToggleMoveMode";
}
