# Changelog

All notable changes to the **Universal Mount System** package will be documented in this file.

## [1.1.1] - 2026-11-19
### Fixed
- Fixed automated approach stalling/oscillating beside tall, wide, or irregular mounts (e.g. lifted pickup trucks, monster trucks, or mounts with default center anchors):
  - `MountApproachPathResolver.ResolveDestination` now projects interior/overhung entry anchors onto a guaranteed-clear exterior standpoint refined against actual mount colliders (zero per-tick heap allocations) and suppresses final-leg repulsion oscillation.
  - `MountInteractionPrompt` checks arrival against `ResolvedDestinationWorld` with generous vertical reach for elevated cabs/saddles.
  - `MountSessionManager.RequestEnter` supports surface-aware distance fallback and height-clamped line-of-sight rays for tall/wide mounts.

## [1.1.0] - 2026-11-19
### Added
- `SeatAttachmentMode.FollowAnchor` (default) on `GenericCharacterControlAdapter` so characters survive mid-ride `Destroy(mount)` and never suffer non-uniform mount scale distortion.
- `CharacterControlAdapterBase.FootPivotOffset` so center-pivoted capsules are placed accurately on ground surfaces without sinking.
- `MountSeat.grantsControlLease` and `MountSessionManager.RequestSwitchSeat(MountSeat)` for multi-seat driver/passenger/gunner mounts.
- `ExitSafetyPolicy` (`GroundedOnly`, `AllowAirborneOrWater`, `Unrestricted`) for boats, aircraft, and skydiving dismounts.
- `MountSessionManager.ForceEnter(target, seat)` and `bypassProximityChecks` parameter on `RequestEnter`.
- `UnityCompat` cross-version helpers (`linearVelocity`, `FindFirstObjectByType`, `FindObjectsByType`) for zero-warning compilation on Unity 2021.3 through Unity 6.
- Scene-View Gizmos (`OnDrawGizmosSelected`) on `MountableControlAdapterBase` for `seatAnchor`, `entryApproachAnchor`, and `exitCandidateAnchors`.
- `ManualMovementInputProvider` delegate on `GenericCharacterControlAdapter` and `InputAuthorityRouter.TriggerInteraction()` for New Input System projects.

### Fixed
- Guarded all legacy `UnityEngine.Input.*` calls with `#if ENABLE_LEGACY_INPUT_MANAGER` to prevent `InvalidOperationException` in New Input System-only projects (Unity 6 default).
- Fixed `CameraAuthority` silently skipping Cinemachine virtual cameras (`OutputCamera == null`) or disabling `MainCamera` when switching to a virtual camera rig; added Cinemachine 2 `LookAt` and Cinemachine 3 `Target.TrackingTarget` support.
- Fixed moving-exit coasting bug where `MountSessionManager` re-read `exitTelemetry.Velocity` after `NeutralizeInputs()` zeroed it.
- Fixed character death while mounted leaking seat occupancy, control lease, and `"DEATH"` input suspension on revive.
- Fixed `MountSessionManager` ignoring `MountableControlAdapterBase.CurrentSpeedKmh` overrides and conflicting speed limits.
- Fixed early guard failures in `MountSessionManager` bypassing `OnTransitionFailed` / `onTransitionFailedEvent`.
- Fixed premature `ResumeInput("TRANSITION")` re-enabling the old target mid-commit.
- Fixed `GenericCharacterControlAdapter` setting `Rigidbody.velocity` in `Update()` instead of `FixedUpdate()` during automated approach.
- Eliminated per-tick `string.Format` GC allocation in `MountInteractionPrompt`, unified `obstacleLayerMask`, moved `MountIcon` onto `IMobileControlUIProvider`, wired `allowProximityAutoEntry`, and made `WorldRelevanceResolver` opt-in.

### Removed
- Removed unused `ControlMode.Remote` / `IRemoteControllable` stubs and unreferenced `transitionTimeoutSeconds` / `useUnscaledTime` settings fields.

## [1.0.0] - 2026-11-19
- Initial public release under MIT License.
