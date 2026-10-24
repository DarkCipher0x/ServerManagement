# Dcx.Ai

Everything AI, as one module: **Ai/** the engines (`AiClient` with one file per engine under `Engines/`), the agent loop, the operator's chat side, report analysis, help answers, vision, the resource guard, Foundry Local; **Views/** the AI Engineer sidebar (AiDock, AiPanel, AiWindow), the AI operator screen, the send preview, screenshot analysis, the AI's Settings cards and `AiModule`, which contributes all of it through the module contracts.

May reference: every project below the app. Referenced only by the app's `App.LoadAiModule()` (never inlined) and its self-test: with AI features off this assembly is never loaded.
