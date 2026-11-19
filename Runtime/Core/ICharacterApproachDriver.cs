using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Optional capability implemented by a character adapter to drive automated walking/running
    /// toward a mount's entry anchor (around obstacles/mount colliders) before boarding.
    /// Implement this on any third-person controller adapter to support GTA-style walk-to-door mounting;
    /// if absent, boarding triggers immediately when within interactionRange.
    /// </summary>
    public interface ICharacterApproachDriver : IControlCapability
    {
        /// <summary>
        /// Returns true if the character is currently able to start an automated approach.
        /// </summary>
        bool CanStartApproach { get; }

        /// <summary>
        /// Returns true while the approach remains valid (e.g. character is alive, not locked/ragdolled).
        /// </summary>
        bool IsApproachStateValid { get; }

        /// <summary>
        /// Called once when automated approach begins. Lock player locomotion input here.
        /// </summary>
        bool BeginApproachDrive();

        /// <summary>
        /// Called during approach updates to steer the character toward the next resolved world waypoint.
        /// </summary>
        void DriveApproachStep(Vector3 nextWaypointWorld, float deltaTime);

        /// <summary>
        /// Called when the approach completes or is cancelled. Restore player locomotion input here.
        /// </summary>
        void EndApproachDrive();

        /// <summary>
        /// Reads the player's raw manual movement axes so manual movement cancels the automated approach.
        /// </summary>
        Vector2 ReadManualMovementInput();
    }
}
