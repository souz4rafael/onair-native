import { action } from "@elgato/streamdeck";
import { MomentaryActionBase } from "./momentary-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.scroll-down" })
export class ScrollDownAction extends MomentaryActionBase {
	protected readonly command: OnAirAction = "ScrollDown";
}
