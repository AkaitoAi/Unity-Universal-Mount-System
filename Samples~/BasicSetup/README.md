# Basic Setup Sample

These three minimal scripts demonstrate how to connect **any** third-person controller, **any** mount, and **any** camera to the Universal Mount System in under a minute:

1. **Manager**: Create an empty GameObject `MountSessionManager` and add:
   - `MountSessionManager`
   - `MountInteractionPrompt` (optional: assign a UI prompt/button)
   - `CameraAuthority` and `InputAuthorityRouter` are auto-added if omitted.
2. **Player**: Create a Capsule with `CharacterController`, `SimpleThirdPersonController`, and `GenericCharacterControlAdapter`.
   - Drag `SimpleThirdPersonController` into `GenericCharacterControlAdapter -> Control Behaviours`.
3. **Mount(s)**: Create any number of Cubes/Vehicles/Animals with `Rigidbody`, `SimpleMountController`, and `GenericMountControlAdapter`.
   - Drag `SimpleMountController` into `GenericMountControlAdapter -> Control Behaviours`.
   - Add one or more `Seats` with a `seatAnchor`, `entryApproachAnchor`, and optional `exitCandidateAnchors`.
   - Set `Hide Occupant Visuals` to `true` for enclosed vehicles or `false` for visible riders (horses, motorcycles, turrets).
4. **Camera**: Add `SimpleFollowCamera` to your Main Camera and assign it to `CameraAuthority -> On Foot Camera Rig -> Camera Script`.
5. Press **Play**, walk up to any mount, and press **`E`** (or **`F`** / Gamepad **South**) to mount and dismount!
