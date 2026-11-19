using UnityEngine;

namespace AkaitoAi.UniversalMountSystem.Configuration
{
    [CreateAssetMenu(fileName = "MountSessionSettings", menuName = "Universal Mount System/Session Settings", order = 10)]
    public class MountSessionSettingsSO : ScriptableObject
    {
        [Header("Interaction & Proximity")]
        [Tooltip("Maximum distance in meters from the seat's entry anchor at which boarding may complete.")]
        [Min(0.5f)] public float interactionRange = 3.5f;

        [Tooltip("Maximum gap from the character capsule to the mount body at which interaction can select it and approach its entry point.")]
        [Min(0.5f)] public float approachSelectionRange = 1.5f;

        [Tooltip("Distance from the entry point at which the character boards after approaching.")]
        [Min(0.2f)] public float approachStoppingDistance = 0.8f;

        [Tooltip("Cancel an automated approach if the character cannot make progress for this many seconds.")]
        [Min(0.5f)] public float approachStuckTimeout = 3f;

        [Tooltip("Maximum mount speed in km/h to allow boarding.")]
        [Min(0f)] public float maxBoardingSpeedKmh = 5.0f;

        [Tooltip("Maximum mount speed in km/h to allow safe normal exit.")]
        [Min(0f)] public float maxExitSpeedKmh = 5.0f;

        [Header("Moving Exit (Optional)")]
        [Tooltip("Allow a moving bailout above maxExitSpeedKmh when the character adapter supports CanExitMoving.")]
        public bool allowMovingExit = true;

        [Tooltip("Maximum mount speed in km/h for a controlled roll exit before full bailout/ragdoll.")]
        [Min(5f)] public float movingExitRollMaxSpeedKmh = 25.0f;

        [Tooltip("Lateral impulse (m/s) pushing the exiting character away from the mount body.")]
        [Min(0f)] public float movingExitOutwardImpulse = 3.5f;

        [Tooltip("Upward impulse (m/s) giving the exiting character a dive arc off the ground.")]
        [Min(0f)] public float movingExitUpwardImpulse = 3.2f;

        [Tooltip("Optional brief airborne dive duration (seconds) before ragdoll activates on high-speed bailouts.")]
        [Range(0f, 0.6f)] public float movingExitDiveDuration = 0f;

        [Tooltip("Rotational tumble impulse (rad/s) applied on high-speed bailouts.")]
        [Min(0f)] public float movingExitTumbleTorque = 10.0f;

        [Tooltip("Minimum seconds before the character recovers after a moving bailout.")]
        [Min(0.1f)] public float movingExitRecoveryDelay = 1.5f;

        [Header("Unoccupied Mount Coasting")]
        [Tooltip("Deceleration (m/s^2) applied to an unmounted moving mount so it coasts and stops naturally.")]
        [Min(0.5f)] public float mountCoastDeceleration = 5.0f;

        [Tooltip("Maximum seconds an unmounted mount may coast before parking.")]
        [Min(0.5f)] public float mountCoastMaxDuration = 4.0f;

        [Tooltip("Require explicit button press to enter; proximity alone will not auto-enter.")]
        public bool allowProximityAutoEntry = false;

        [Header("Exit Clearance & Safety Checks")]
        [Tooltip("Capsule radius used for exit clearance sweep/overlap testing against walls and obstacles.")]
        [Min(0.1f)] public float characterCapsuleRadius = 0.4f;

        [Tooltip("Capsule height used for exit clearance testing.")]
        [Min(0.5f)] public float characterCapsuleHeight = 1.8f;

        [Tooltip("Downward ray distance in meters to find solid ground beneath candidate exit points.")]
        [Min(0.5f)] public float groundCheckDistance = 2.5f;

        [Tooltip("Maximum allowed slope angle of ground surface beneath exit point.")]
        [Range(10f, 80f)] public float maxGroundSlopeAngle = 45.0f;

        [Tooltip("Layer mask for ground detection.")]
        public LayerMask groundLayerMask = ~0;

        [Tooltip("Layer mask for obstacle and wall collision testing on exit and line-of-sight.")]
        public LayerMask obstacleLayerMask = ~0;

        [Header("Transaction & Diagnostics")]
        [Tooltip("Timeout in seconds before an incomplete transition rolls back.")]
        [Min(1f)] public float transitionTimeoutSeconds = 3.0f;

        [Tooltip("Whether transition timeouts evaluate using unscaled time.")]
        public bool useUnscaledTime = true;

        public bool enableDebugLogs = false;
    }
}
