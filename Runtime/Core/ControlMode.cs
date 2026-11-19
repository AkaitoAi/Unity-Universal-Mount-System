namespace AkaitoAi.UniversalMountSystem
{
    public enum ControlMode
    {
        OnFoot,
        Mounted
    }

    public enum TransitionPhase
    {
        Ready,
        Validating,
        Reserved,
        Quiesced,
        Preparing,
        Committing,
        RollingBack,
        Recovering
    }

    public enum ExitSafetyPolicy
    {
        /// <summary>Require solid ground within slope limits and clear capsule space (cars, bikes, horses).</summary>
        GroundedOnly,
        /// <summary>Allow exiting over water or mid-air when no ground is found, while still checking obstacle overlap (boats, aircraft).</summary>
        AllowAirborneOrWater,
        /// <summary>Skip ground and obstacle clearance checks and place directly at the candidate anchor.</summary>
        Unrestricted
    }

    public enum SeatAttachmentMode
    {
        /// <summary>
        /// Syncs character pose to the seat anchor each frame without reparenting.
        /// Prevents non-uniform mount scale distortion and ensures the character survives if the mount is destroyed mid-ride.
        /// </summary>
        FollowAnchor,
        /// <summary>
        /// Reparents the character under the seat anchor (restoring original localScale on detach).
        /// </summary>
        ParentToAnchor
    }
}
