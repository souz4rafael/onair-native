import { SingletonAction, type KeyDownEvent, type WillAppearEvent } from "@elgato/streamdeck";
import { onairClient, type RemoteState } from "../onair-client.js";

type BoolFieldName =
	| "showFollowUpsInInsights"
	| "showExternalInsightsInInsights"
	| "showPacingInInsights"
	| "showTokenUsageInInsights";

/**
 * Shared base for the 4 AI Insights section-visibility toggles (Questions/External
 * Insights/Pacing/Token Usage). Unlike {@link ToggleActionBase}'s fixed HotkeyAction, these
 * fields have no dedicated hotkey — they're plain on/off config values only reachable through
 * the WebSocket "set" op (mirrors mcp-server/OnAirTools.cs's onair_toggle_insights_show_* tools
 * and the Web Remote's app.js wireToggleFields()). A key press reads the field's last known
 * value off the cached {@link RemoteState}, negates it, and pushes the negation back via
 * {@link onairClient.setField}; the resulting state broadcast — not the press itself — is what
 * updates the key's 2-state icon, the same "server is the source of truth" flow as
 * ToggleActionBase.
 */
export abstract class FieldToggleActionBase extends SingletonAction {
	protected abstract readonly stateField: BoolFieldName;
	/** PascalCase field name expected by RemoteControlService's "set" op — e.g. "ShowPacingInInsights". */
	protected abstract readonly field: string;

	private subscribed = false;
	private readonly lastApplied = new Map<string, 0 | 1>();

	override onWillAppear(ev: WillAppearEvent): void | Promise<void> {
		if (!this.subscribed) {
			this.subscribed = true;
			onairClient.onState((state) => this.syncAll(state));
		}
		const state = onairClient.currentState;
		if (state && ev.action.isKey()) {
			return this.applyIfChanged(ev.action.id, ev.action.setState.bind(ev.action), state[this.stateField] ? 1 : 0);
		}
	}

	override onKeyDown(_ev: KeyDownEvent): void {
		const current = onairClient.currentState?.[this.stateField] ?? false;
		onairClient.setField(this.field, !current);
	}

	private syncAll(state: RemoteState): void {
		for (const a of this.actions) {
			if (a.isKey()) this.applyIfChanged(a.id, a.setState.bind(a), state[this.stateField] ? 1 : 0);
		}
	}

	private applyIfChanged(id: string, setState: (value: 0 | 1) => Promise<void>, value: 0 | 1): void {
		if (this.lastApplied.get(id) === value) return;
		this.lastApplied.set(id, value);
		void setState(value);
	}
}
