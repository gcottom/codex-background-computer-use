# Background Computer Use

Background Computer Use is a local Windows plugin for inspecting and operating application windows while they remain behind other windows. It helps agents check status and interact with supported controls without activating the target window or taking over the user's mouse. This can reduce interruptions when the user needs to keep another app in front, and makes it easier to verify what changed by returning a fresh observation after an action.

The plugin combines Windows Graphics Capture for covered-window images, accessibility information for controls, and targeted window-message actions. It can read supported text and button state, change control values, and click coordinates within a captured window. It also offers an explicit handoff to the existing foreground Computer Use workflow when an app requires physical mouse input.

## Capabilities

- Capture covered windows without restoring or activating them.
- Read current UI controls and perform supported background control actions.
- Keep observations tied to the exact window and process, and reject stale action references.
- Return fresh images and state so agents can check the effect of each operation.
- Offer foreground mouse handoff for apps that do not support background input.

Some games, GPU-rendered apps, raw-input interfaces, minimized windows, and protected surfaces may not support background capture or control. The plugin reports these limits and does not silently switch to foreground input. A successful window message alone does not confirm that the application accepted the action.

## Install from this marketplace

Add this private repository as a Codex plugin marketplace:

```powershell
codex plugin marketplace add gcottom/codex-background-computer-use
codex plugin add background-computer-use@background-computer-use-marketplace
```

The local native helper is required for Windows capture and control. Read the [plugin setup and usage guide](plugins/background-computer-use/README.md) to configure a trusted helper and check which capture and input paths work in your environment.
