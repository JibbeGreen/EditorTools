# Greeny ProUI

ProUI is a highly decoupled, modular UI package meant to be shared across multiple Greeny projects.

## Architecture

This package leverages **Dependency Inversion** and an **Event-Driven Architecture** to ensure that UI elements do not strictly depend on game-specific implementations (e.g., specific `SceneManager` calls, specific `ActorFacade` singletons, or exact layers).

### How it communicates with the Game

1. **Outbound (UI -> Game):** 
   When the user interacts with the UI (e.g., clicking "Start Game" on the Main Menu), the UI raises an event via `EventBus<T>`. 
   *Example:* `EventBus<UI_StartGameRequestEvent>.Raise(...)`

2. **Inbound (Game -> UI):** 
   When the game state updates (e.g., loading progress advances), the game fires an event that the UI is listening for.
   *Example:* `EventBus<UI_LoadingProgressEvent>.Raise(...)`

3. **Visitor Pattern (Contextual Data):**
   For elements like the Crosshair, the UI performs a raycast and visits the hit object using the `IVisitor` pattern. The object can then implement `IReticleProvider` to pass back the necessary `ReticleData` to the UI, isolating game state rules from the UI package.

## Integration

### 1. Events
All UI-specific events are centralized in `Assets/Greeny/ProUI/Core/EventsUI.cs`. This ensures they don't clash with or get overridden by other packages.

### 2. Required Packages
To use this package in your project, it requires the core systems (specifically `EventBus` and `IVisitor` interfaces). 

> **Note:** If you are using Unity Package Manager (UPM) to pull this from Git, make sure you also pull your `Greeny.Core` package. You can explicitly link it in this repository's `package.json` under `dependencies` once your core package has a finalized name (e.g. `"com.greeny.core": "1.0.0"`).

## Setup in new projects

1. Add this git repository URL to your Unity project's `manifest.json`.
2. Implement listeners for `UI_StartGameRequestEvent` inside your game's Scene Management system to handle the actual loading logic.
3. Fire `UI_LoadingProgressEvent` from your loading coroutine so the Main Menu loading bar updates.
4. Implement `IReticleProvider` and `IVisitable` on any interactable/enemy GameObjects to make the crosshair contextual.
