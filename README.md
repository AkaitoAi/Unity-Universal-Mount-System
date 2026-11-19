# Unity Universal Mount System

A controller-agnostic, zero-dependency, transactional **Mount & Third-Person Interaction System** for Unity (`2021.3+` / `Unity 6`).

Designed to drop cleanly into **any Unity game** with:
- **Any third-person controller** (Unity Starter Assets, Invector, Opsive, Kinematic Character Controller, or custom `CharacterController` / `Rigidbody` scripts).
- **Any type of mount** (cars, motorcycles, horses, dragons, boats, helicopters, mechs, turrets, hoverboards).
- **Any number of mounts & seats** (automatically discovered via a zero-allocation `MountableRegistry`, supporting multi-seat driver/passenger mounts with seat switching).

---

## Key Features

- **Transactional Control Handoff (`ControlSessionCoordinator`)** — Atomic `Validate -> Reserve -> Quiesce -> Prepare -> Commit` transitions with automatic rollback, multi-seat switching (`RequestSwitchSeat`), and safe recovery if a mount is destroyed mid-ride.
- **Zero Third-Party Dependencies & Unity 6 Ready** — Compiles with zero warnings across Unity 2021.3, 2022.3, and Unity 6. Safe in both Legacy Input Manager and New Input System-only projects, and supports both `UnityEngine.UI.Text` and `TextMeshPro` without requiring a compile-time TMP reference.
- **Scale-Safe, Non-Destructive Attachment (`SeatAttachmentMode.FollowAnchor`)** — Syncs the character to the seat anchor without reparenting by default, preventing non-uniform mount scale distortion and guaranteeing the player survives if the mount is destroyed mid-ride.
- **GTA-Style Obstacle-Aware Approach (`MountApproachPathResolver`)** — Automatically plans a zero-allocation perimeter path around the mount's colliders to reach the seat's entry anchor, and cancels immediately if the player moves manually.
- **Flexible Exit Safety (`ExitSafetyPolicy`)** — Supports `GroundedOnly` (ground/slope/capsule overlap & sweep checks), `AllowAirborneOrWater` (boats & aircraft), and `Unrestricted`, with automatic `FootPivotOffset` so center-pivoted capsules never sink into the floor.
- **Moving Exit & Unoccupied Coasting** — Optional high-speed bailout impulses and natural mount coasting deceleration.
- **Unified Camera & Audio Authority (`CameraAuthority`)** — Supports single shared follow cameras, Cinemachine 2/3 virtual cameras, per-mode rigs, or per-mount custom camera rigs (`ICameraRigProvider`), guaranteeing a single active `MainCamera` and `AudioListener`.
- **Scene-View Gizmo Authoring** — Visualizes `seatAnchor` (cyan), `entryApproachAnchor` (green), and `exitCandidateAnchors` (yellow) directly in the Unity Scene View.
- **Token-Based Input Suspension (`InputAuthorityRouter`)** — Stackable suspension tokens (`PAUSE`, `MENU`, `CUTSCENE`, `DEATH`, `FOCUS_LOST`).

---

## Installation (Unity Package Manager)

1. Open **Window → Package Manager** in Unity.
2. Click **`+` → Add package from git URL...**
3. Enter:
   ```
   https://github.com/AkaitoAi/Unity-Universal-Mount-System.git
   ```
4. *(Optional)* Import the **Basic Setup Examples** sample from the package's **Samples** tab to test in a blank scene immediately.

---

## Quick Start (No Code Required)

### 1. Scene Manager
Create an empty GameObject named `MountSessionManager` and attach:
- `MountSessionManager`
- `MountInteractionPrompt` *(optional: wire a UI prompt panel, text, icon, or mobile button)*

`CameraAuthority` and `InputAuthorityRouter` are automatically attached if not already present.

### 2. Third-Person Character
On your player character GameObject, attach **`GenericCharacterControlAdapter`**:
- Add your third-person controller's locomotion/input scripts to **Control Behaviours** (they will be disabled automatically while mounted and re-enabled on dismount).
- `CharacterController`, `Rigidbody`, `CapsuleCollider`, and `Animator` are auto-detected.
- Leave **Attachment Mode** on `FollowAnchor` (recommended) to avoid scale warping and survive mount destruction.
- *(Optional)* Set **Mounted Bool Parameter** / **Seat Index Int Parameter** to drive seated animations, or hook **On Mounted / On Dismounted** UnityEvents.

### 3. Mounts (Add as Many as You Want)
On **each** mountable GameObject (car, bike, horse, boat, aircraft, turret, etc.), attach **`GenericMountControlAdapter`**:
- Add your mount's driving/riding/flying scripts to **Control Behaviours** (automatically disabled when unoccupied, enabled when mounted).
- Add any number of **Seats** in the Inspector:
  - `seatAnchor` — Transform where the character sits (cyan gizmo).
  - `entryApproachAnchor` — Transform where the character walks before boarding (green gizmo).
  - `exitCandidateAnchors` — Preferred dismount points (yellow gizmos; falls back to left/right/rear if omitted or blocked).
  - `hideOccupantVisuals` — `true` for enclosed cabins/cars, `false` for visible riders (horses, motorcycles, open turrets).
  - `grantsControlLease` — `true` for driver/pilot seats, `false` for passenger seats.
- Set **Exit Safety Policy** (`GroundedOnly` for land vehicles/animals, `AllowAirborneOrWater` for boats/aircraft).
- *(Optional)* Assign a per-mount **Custom Camera Rig** (separate Camera or Cinemachine Virtual Camera), **Controlled Audio Sources**, **Mobile Controls Root**, **Mount Action Label** (e.g. `"Drive"`, `"Ride"`, `"Pilot"`), and **Mount Icon**.

---

## New Input System Integration (2 Lines)

In projects using Unity's **New Input System** (`Active Input Handling = Input System Package`), bind your `Interact` and `Move` actions cleanly:

```csharp
using AkaitoAi.UniversalMountSystem.Adapters;
using AkaitoAi.UniversalMountSystem.Services;

// Trigger mount/dismount from your Interact InputAction:
interactAction.performed += _ => MountSessionManager.Instance.InputRouter.TriggerInteraction();

// Allow manual stick/WASD movement to cancel automated walk-to-mount approach:
characterAdapter.ManualMovementInputProvider = () => moveAction.ReadValue<Vector2>();
```

---

## Programmatic API

```csharp
using AkaitoAi.UniversalMountSystem;
using AkaitoAi.UniversalMountSystem.Services;

// Mount a specific target (or specific seat)
MountSessionManager.Instance.RequestEnter(mountAdapter);
MountSessionManager.Instance.RequestEnter(mountAdapter, mountAdapter.Seats[1]);

// Start a level/race already seated (bypasses distance/LoS/speed checks)
MountSessionManager.Instance.ForceEnter(mountAdapter);

// Switch seats while mounted (e.g. Passenger -> Driver)
MountSessionManager.Instance.RequestSwitchSeat(mountAdapter.Seats[0]);

// Dismount
MountSessionManager.Instance.RequestExit();

// Suspend/resume input cleanly (e.g. pause menu, dialogue, cutscene)
MountSessionManager.Instance.InputRouter.SuspendInput("PAUSE_MENU");
MountSessionManager.Instance.InputRouter.ResumeInput("PAUSE_MENU");

// Subscribe to lifecycle events
MountSessionManager.Instance.OnMountEntered += (target, seat) => { /* ... */ };
MountSessionManager.Instance.OnMountExited += (target) => { /* ... */ };
MountSessionManager.Instance.OnTransitionFailed += (txId, reason, mode) => { /* ... */ };
```

---

## Extending for Custom Controllers or Mounts

### Custom Third-Person Controller Adapter
Subclass `CharacterControlAdapterBase` (and optionally implement `ICharacterApproachDriver` for custom walk-to-door locomotion).

### Custom Mount Adapter
Subclass `MountableControlAdapterBase` and override `CurrentSpeedKmh`, `NeutralizeInputs()`, and `SetInputActive(bool)`:

```csharp
using AkaitoAi.UniversalMountSystem;

public class MyCustomMountAdapter : MountableControlAdapterBase
{
    public override float CurrentSpeedKmh => myEngine.SpeedKmh;
    public override void NeutralizeInputs() { myEngine.SetThrottle(0f); ApplyGenericControlState(false); }
    public override void SetInputActive(bool active) { myEngine.CanControl = active && HasLease; ApplyGenericControlState(active && HasLease); }
}
```

### Custom Camera Scripts
Implement `ICameraTargetable` on any camera script so `CameraAuthority` retargets it automatically (Cinemachine 2 `Follow`/`LookAt`, Cinemachine 3 `Target.TrackingTarget`, `SetTarget`, `SetMainTarget`, and `target` fields are also supported automatically via cached reflection).

---

## Troubleshooting

- **Character stops or runs in place near a mount during approach:** Check whether your mount has child trigger colliders (e.g. near-miss or interaction zones) on a layer included in your third-person controller's wall-stop raycast mask (such as Invector's `stopMoveLayer`). Ensure your character controller's forward raycasts/capsulecasts pass `QueryTriggerInteraction.Ignore` (or set `Physics.queriesHitTriggers = false`), and enable `MountSessionSettingsSO.enableDebugLogs` to inspect approach coordinates in the Console.

---

## License

This project is licensed under the **MIT License** — see [LICENSE](LICENSE) for details.
