# L.A.R.P.

**L.A.R.P.** means **Likelihood Assessment and Reporting for Power outages**. This WPF prototype for the HACKOHI/O 2026 challenge presents an anonymized location workflow, a relative historical damage index, personalized preparation guidance, opt-in alert previews, and an assistant panel in one window.

## Project layout

- `src/` — L.A.R.P. WPF application and feature code
- `src/Prediction/` — prediction contracts, heuristic service, and aggregated GIS heatmap data
- `src/Assistant/` — chat contract and local stub response
- `src/Configuration/` — OpenAI-compatible provider settings contract

The map uses OpenStreetMap tiles with an optional, toggleable historical GIS coverage layer for southwest Virginia. Select a location and press **View my estimate** to refresh the relative risk index, repair-work scenario, and preparation guidance. The score is heuristic and is not a calibrated outage probability or live utility forecast. Alert previews follow documented prototype rules but do not send messages; official utility events are not connected. The assistant returns local default text and does not call a model.

## API configuration scaffold

Future integrations can read these environment variables without storing credentials in source control:

- `OUTAGE_AI_BASE_URL` — OpenAI-compatible API base URL
- `OUTAGE_AI_MODEL` — model identifier
- `OUTAGE_AI_API_KEY` — secret API key

`OpenAiCompatibleApiOptions.FromEnvironment()` returns `null` until all settings are present. No API requests are currently made.

## Run

Open `larp.slnx` in Visual Studio with the .NET 10 Windows Desktop workload, or run `dotnet run --project larp.csproj` on Windows.
