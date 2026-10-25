# L.A.R.P.

**L.A.R.P.** means **Likelihood Assessment and Reporting for Power outages**. This WPF prototype for the HACKOHI/O 2026 challenge presents an anonymized location workflow, a relative historical damage index, personalized preparation guidance, opt-in alert previews, and an assistant panel in one window.

## Project layout

- `src/` — L.A.R.P. WPF application and feature code
- `src/Prediction/` — prediction contracts, heuristic service, and aggregated GIS heatmap data
- `src/Assistant/` — OpenAI-compatible chat completions client and conversation contract
- `src/Configuration/` — local OpenAI-compatible provider settings

The map uses OpenStreetMap tiles with an optional, toggleable historical GIS coverage layer for southwest Virginia. Select a location and press **View my estimate** to refresh the relative risk index, repair-work scenario, and preparation guidance. The score is heuristic and is not a calibrated outage probability or live utility forecast. Alert previews follow documented prototype rules but do not send messages; official utility events are not connected. The assistant calls an OpenAI-compatible Chat Completions endpoint when configured and keeps a short conversation in memory for the current app session.

## AI assistant setup

Copy `.env.example` to `.env` in the repository root. For OpenAI, replace the API key placeholder and restart L.A.R.P. For a local OpenAI-compatible server, set `OUTAGE_AI_BASE_URL=localhost:PORT/v1` and `OUTAGE_AI_MODEL` to the model name; a key can be omitted for loopback providers. Both forms such as `localhost:1234/v1` and `http://localhost:1234/v1` are accepted. The local `.env` file is ignored by Git.

The assistant uses these settings:

- `OUTAGE_AI_BASE_URL` — API base URL (default `https://api.openai.com/v1`; local servers can use `localhost:PORT/v1`)
- `OUTAGE_AI_MODEL` — model identifier (default `gpt-5-mini`)
- `OUTAGE_AI_API_KEY` — local API key

The app also reads these values from the process environment. Keys are not stored in app settings or source code. Requests are sent directly from the desktop app to the configured provider; chat text and the current estimate context are sent to that provider for response generation.

## Run

Open `larp.slnx` in Visual Studio with the .NET 10 Windows Desktop workload, or run `dotnet run --project larp.csproj` on Windows.
