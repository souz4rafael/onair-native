import { action, SingletonAction, type KeyAction, type WillAppearEvent } from "@elgato/streamdeck";
import { onairClient, type RemoteState } from "../onair-client.js";

/** Maps RemoteState.pacingLevel ("None" | "Slow" | "Good" | "Fast") to the pre-rendered gauge
 * icon that represents it — see gen_icons.py's draw_gauge for how each variant was generated
 * (gray/no needle for "no data yet", yellow/needle-left for slow, green/needle-center for good,
 * red/needle-right for fast). Falls back to the neutral "None" icon for any unrecognized value
 * so a future new level (or a stale client) never renders a blank/broken key image. */
const ICON_BY_LEVEL: Record<string, string> = {
	Slow: "imgs/actions/pacing-status/slow",
	Good: "imgs/actions/pacing-status/good",
	Fast: "imgs/actions/pacing-status/fast",
	None: "imgs/actions/pacing-status/none",
};

/**
 * Read-only speaking-pace status tile: shows the most recently completed Q&A recording's pacing
 * as a color-coded speedometer icon (gray = not enough data yet, yellow = a bit slow, green =
 * good pace, red = a bit fast) — see PacingAnalyzer's WPM thresholds for the exact
 * classification. Purely a glanceable, at-a-glance display, same "read-only" philosophy as
 * StatusAction; there's nothing meaningful to trigger from a press, so onKeyDown is
 * intentionally not overridden.
 *
 * Uses setImage() rather than the manifest's States/setState() mechanism — the SDK's own doc
 * comments describe setState as strictly a 0/1 toggle, unsuited to this genuine 4-way status —
 * driven entirely by RemoteState.pacingLevel instead. Redundant setImage calls are skipped (via
 * `lastApplied`, mirroring ToggleActionBase's dedupe) since a state push arrives at least every
 * 2s from onAIr's safety-net timer regardless of whether anything actually changed.
 */
@action({ UUID: "com.souz4rafael.onair.pacing-status" })
export class PacingStatusAction extends SingletonAction {
	private subscribed = false;
	private readonly lastApplied = new Map<string, string>();

	override onWillAppear(ev: WillAppearEvent): void | Promise<void> {
		if (!this.subscribed) {
			this.subscribed = true;
			onairClient.onState((state) => this.syncAll(state));
		}
		const state = onairClient.currentState;
		if (state && ev.action.isKey()) return this.applyIfChanged(ev.action, state);
	}

	private syncAll(state: RemoteState): void {
		for (const a of this.actions) {
			if (a.isKey()) this.applyIfChanged(a, state);
		}
	}

	private applyIfChanged(a: KeyAction, state: RemoteState): void {
		const icon = ICON_BY_LEVEL[state.pacingLevel] ?? ICON_BY_LEVEL.None;
		if (this.lastApplied.get(a.id) === icon) return;
		this.lastApplied.set(a.id, icon);
		void a.setImage(icon);
	}
}
