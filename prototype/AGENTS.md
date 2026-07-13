# Prototype Instructions

Run the local server yourself and open the preview in the browser available to this environment. Do not give the user server-start instructions when you can run it.

Before making substantial visual changes, use the Product Design plugin's `get-context` skill when the visual source is unclear or no longer matches the current goal. When the user gives durable prototype-specific design feedback, preferences, or decisions, record them in `AGENTS.md`.

When implementing from a selected generated mock, treat that image as the source of truth for layout, component anatomy, density, spacing, color, typography, visible content, and hierarchy.

## Confirmed product and design decisions

- Product name is `祖名闪电说`.
- This folder is a clickable UI-only handoff prototype, not the production Windows implementation.
- Preserve the selected bright daylight-future direction: pearl white, ice blue, cyan, azure, and soft violet.
- The recording capsule is compact (204 × 58 px), translucent white optical glass, with blue-violet text and cyan-violet waveform bars.
- The capsule is not part of the main window. In production it is a separate topmost, non-activating Windows overlay that is hidden when idle and appears only after the global hotkey is pressed.
- Required demo interactions: home/settings navigation, record actions, more menu, details drawer, copy feedback, delete undo, connection test, compatibility controls, and recording-state transitions.
- The production application remains .NET 10 WPF; do not turn this prototype into the final Electron/web architecture.
