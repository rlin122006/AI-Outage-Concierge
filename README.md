# AI Outage Concierge

A WPF prototype for the HACKOHI/O 2026 AI Outage Concierge challenge. It presents an anonymized location workflow, customer needs, an outage outlook, preparation guidance, alert preferences, and an assistant panel in one window.

## Project layout

- `src/` — WPF application and feature code
- `src/Prediction/` — prediction request/result contracts and placeholder service
- `src/Assistant/` — chat contract and local stub response
- `src/Configuration/` — OpenAI-compatible provider settings contract

The map is a blank coordinate grid. Click to set a prototype point. The estimate is illustrative and does not use a heatmap, live storm conditions, utility systems, or restoration data. Alert checkboxes record only a prototype preference; they do not send notifications. The assistant returns local default text and does not call a model.

## API configuration scaffold

Future integrations can read these environment variables without storing credentials in source control:

- `OUTAGE_AI_BASE_URL` — OpenAI-compatible API base URL
- `OUTAGE_AI_MODEL` — model identifier
- `OUTAGE_AI_API_KEY` — secret API key

`OpenAiCompatibleApiOptions.FromEnvironment()` returns `null` until all settings are present. No API requests are currently made.

## Run

Open `AI Outage Concierge.slnx` in Visual Studio with the .NET 10 Windows Desktop workload, or run `dotnet run --project "AI Outage Concierge.csproj"` on Windows.
