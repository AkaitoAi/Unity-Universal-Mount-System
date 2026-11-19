using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Capability interface for retrieving spatial motion telemetry without casting to Rigidbody or vendor classes.
    /// </summary>
    public interface IMotionTelemetry : IControlCapability
    {
        Vector3 Velocity { get; }
        float ForwardSpeed { get; }
        Transform MotionAnchor { get; }
    }
}
