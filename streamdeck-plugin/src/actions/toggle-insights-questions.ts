import { action } from "@elgato/streamdeck";
import { FieldToggleActionBase } from "./field-toggle-action-base.js";

@action({ UUID: "com.souz4rafael.onair.toggle-insights-questions" })
export class ToggleInsightsQuestionsAction extends FieldToggleActionBase {
	protected readonly stateField = "showFollowUpsInInsights" as const;
	protected readonly field = "ShowFollowUpsInInsights";
}
