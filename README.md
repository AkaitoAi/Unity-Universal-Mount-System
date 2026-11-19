# Unity Universal Mount System

A controller-agnostic, zero-dependency, transactional **Mount & Third-Person Interaction System** for Unity (`2021.3+` / `Unity 6`).

Designed to drop cleanly into **any Unity game** with:
- **Any third-person controller** (Unity Starter Assets, Invector, Opsive, Kinematic Character Controller, or custom `CharacterController` / `Rigidbody` scripts).
- **Any type of mount** (cars, motorcycles, horses, dragons, boats, helicopters, mechs, turrets, hoverboards).
- **Any number of mounts & seats** (automatically discovered via a zero-allocation `MountableRegistry`, supporting multi-seat mounts with nearest-seat selection).

---

## Key Features

- **Transactional Control Handoff (`ControlSessionCoordinator`)** — Atomic `Validate -> Reserve -> Quiesce -> Prepare -> Commit` transitions with automatic rollback on failure and safe recovery if a mount is destroyed mid-ride.
- **Zero Third-Party Dependencies** — Compiles cleanly out of the box with no external asset store packages required. Works with both legacy `UnityEngine.UI.Text` and `TextMeshPro` without requiring a compile-time TMP reference.
- **GTA-Style Obstacle-Aware Approach (`MountApproachPathResolver`)** — Automatically plans a zero-allocation perimeter path around the mount's colliders to reach the seat's entry anchor, and cancels immediately if the player moves manually.
- **Safe Exit Clearance Verification** — Probes candidate exit anchors (plus automatic left/right/rear fallbacks) for solid ground, walkable slope angle, character capsule overlap, and capsule sweep clearance before dismounting.
- **Moving Exit & Unoccupied Coasting** — Optional high-speed bailout impulses and natural mount coasting deceleration.
- **Unified Camera & Audio Authority (`CameraAuthority`)** — Supports single shared follow cameras, per-mode rigs, or per-mount custom camera rigs (`ICameraRigProvider`), guaranteeing a single active `MainCamera` and `AudioListener`.
- **Token-Based Input Suspension (`InputAuthorityRouter`)** — Stackable suspension tokens (`PAUSE`, `MENU`, `CUTSCENE`, `DEATH`, `FOCUS_LOST`) so closing one menu never accidentally re-enables gameplay input while another lock is active.
- **Desktop, Gamepad, & Mobile UI Ready** — Built-in proximity detection, line-of-sight checks, per-mount action labels & icons, and contextual UI root switching.

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

`CameraAuthority`, `InputAuthorityRouter`, and `WorldRelevanceResolver` are automatically attached if not already present.

### 2. Third-Person Character
On your player character GameObject, attach **`GenericCharacterControlAdapter`**:
- Add your third-person controller's locomotion/input scripts to **Control Behaviours** (they will be disabled automatically while mounted and re-enabled on dismount).
- `CharacterController`, `Rigidbody`, `CapsuleCollider`, and `Animator` are auto-detected.
- *(Optional)* Set **Mounted Bool Parameter** / **Seat Index Int Parameter** to drive seated animations, or hook **On Mounted / On Dismounted** UnityEvents.

### 3. Mounts (Add as Many as You Want)
On **each** mountable GameObject (car, bike, horse, boat, turret, etc.), attach **`GenericMountControlAdapter`**:
- Add your mount's driving/riding/flying scripts to **Control Behaviours** (automatically disabled when unoccupied, enabled when mounted).
- Add any number of **Seats** in the Inspector:
  - `seatAnchor` — Transform where the character attaches.
  - `entryApproachAnchor` — Transform where the character walks before boarding.
  - `exitCandidateAnchors` — Preferred dismount points (evaluated in order; falls back to left/right/rear if omitted or blocked).
  - `hideOccupantVisuals` — `true` for enclosed cockpits/cars, `false` for visible riders (horses, motorcycles, open turrets).
- *(Optional)* Assign a per-mount **Custom Camera Rig**, **Controlled Audio Sources**, **Mobile Controls Root**, **Mount Action Label** (e.g. `"Drive"`, `"Ride"`, `"Pilot"`), and **Mount Icon**.

Every active mount automatically registers itself in `MountableRegistry` on `OnEnable()` and unregisters on `OnDisable()` — no manual scene lists needed.

---

## Extending for Custom Controllers or Mounts

### Custom Third-Person Controller Adapter
Subclass `CharacterControlAdapterBase` (and optionally implement `ICharacterApproachDriver` for custom walk-to-door locomotion):

```csharp
using AkaitoAi.UniversalMountSystem;
using UnityEngine;

public class MyCustomCharacterAdapter : CharacterControlAdapterBase
{
    public override event System.Action OnCharacterDead;
    public override bool IsAttached => isAttached;
    public override bool IsDead => health <= 0f;
    // Override CaptureState, Attach, Detach, RestoreState, NeutralizeInputs, SetInputActive
}
```

### Custom Mount Adapter
Subclass `MountableControlAdapterBase` for vehicle/mount physics engines that need custom speed telemetry or input neutralization:

```csharp
using AkaitoAi.UniversalMountSystem;
using UnityEngine;

public class MyCustomMountAdapter : MountableControlAdapterBase
{
    public override float CurrentSpeedKmh => myEngine.SpeedKmh;
    public override void NeutralizeInputs() { myEngine.SetThrottle(0f); ApplyGenericControlState(false); }
    public override void SetInputActive(bool active) { myEngine.CanControl = active && HasLease; ApplyGenericControlState(active && HasLease); }
}
```

### Custom Camera Scripts
Implement `ICameraTargetable` on any camera script so `CameraAuthority` retargets it automatically (Cinemachine `Follow`, `SetTarget`, `SetMainTarget`, and `target` fields are also supported automatically via cached reflection):

```csharp
using AkaitoAi.UniversalMountSystem;
using UnityEngine;

public class MyCamera : MonoBehaviour, ICameraTargetable
{
    public void SetCameraTarget(Transform target) => followTarget = target;
}
```

### Programmatic API

```csharp
// Mount a specific target (or specific seat)
MountSessionManager.Instance.RequestEnter(mountAdapter);
MountSessionManager.Instance.RequestEnter(mountAdapter, mountAdapter.Seats[1]);

// Dismount
MountSessionManager.Instance.RequestExit();

// Suspend/resume input cleanly (e.g. pause menu, dialogue, cutscene)
MountSessionManager.Instance.InputRouter.SuspendInput("PAUSE_MENU");
MountSessionManager.Instance.InputRouter.ResumeInput("PAUSE_MENU");

// Subscribe to lifecycle events
MountSessionManager.Instance.OnMountEntered += (target, seat) => { /* ... */ };
MountSessionManager.Instance.OnMountExited += (target) => { /* ... */ };
```

---

## Package Structure

```
├── package.json
├── LICENSE (MIT)
├── README.md
├── Runtime/
│   ├── AkaitoAi.UniversalMountSystem.asmdef
│   ├── Core/
│   │   ├── ControlContext.cs
│   │   ├── ControlMode.cs
│   │   ├── ControlSessionCoordinator.cs
│   │   ├── ControlTargetAdapterBase.cs
│   │   ├── ControlTransitionResult.cs
│   │   ├── CharacterControlAdapterBase.cs
│   │   ├── ICameraRigAdapter.cs
│   │   ├── ICharacterApproachDriver.cs
│   │   ├── ICharacterAttachment.cs
│   │   ├── IControlCapability.cs
│   │   ├── IControlTarget.cs
│   │   ├── IMotionTelemetry.cs
│   │   ├── IMountable.cs
│   │   ├── IRemoteControllable.cs
│   │   ├── MountSeat.cs
│   │   └── MountableControlAdapterBase.cs
│   ├── Configuration/
│   │   └── MountSessionSettingsSO.cs
│   ├── Services/
│   │   ├── CameraAuthority.cs
│   │   ├── InputAuthorityRouter.cs
│   │   ├── MountApproachPathResolver.cs
│   │   ├── MountSessionManager.cs
│   │   └── WorldRelevanceResolver.cs
│   ├── Adapters/
│   │   ├── GenericCharacterControlAdapter.cs
│   │   └── GenericMountControlAdapter.cs
│   └── UI/
│       └── MountInteractionPrompt.cs
└── Samples~/
    └── BasicSetup/
        ├── README.md
        ├── SimpleFollowCamera.cs
        ├── SimpleMountController.cs
        └── SimpleThirdPersonController.cs
```

---

## License

This project is licensed under the **MIT License** — see [LICENSE](LICENSE) for details.
