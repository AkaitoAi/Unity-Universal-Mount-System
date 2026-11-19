using System;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    public struct MovingExitParams
    {
        public Vector3 baseVelocity;
        public Vector3 outwardDirection;
        public Transform exitedMountRoot;
        public float rollMaxSpeedKmh;
        public float outwardImpulse;
        public float upwardImpulse;
        public float diveDuration;
        public float tumbleTorque;
        public float recoveryDelay;
    }

    /// <summary>
    /// Abstract base class for any on-foot character control target.
    /// Decouples <see cref="Services.MountSessionManager"/> from any specific third-person controller
    /// (Starter Assets, Invector, Opsive, Kinematic Character Controller, or custom controllers).
    /// </summary>
    public abstract class CharacterControlAdapterBase : ControlTargetAdapterBase, ICharacterAttachment, IMotionTelemetry
    {
        public abstract event Action OnCharacterDead;

        public abstract bool IsAttached { get; }
        public abstract bool IsDead { get; }
        public virtual bool IsGrounded => true;
        public virtual CapsuleCollider CapsuleCollider => null;
        public virtual Rigidbody Rigidbody => null;
        public virtual bool CanExitMoving => false;
        public virtual bool CanBoard => !IsDead;

        public virtual void ApplyMovingExit(Vector3 velocity, float recoveryDelay) { }

        public virtual void ApplyMovingExit(MovingExitParams exitParams) =>
            ApplyMovingExit(exitParams.baseVelocity, exitParams.recoveryDelay);

        public virtual Vector3 Velocity => Rigidbody != null ? Rigidbody.velocity : Vector3.zero;
        public virtual float ForwardSpeed => Rigidbody != null ? Vector3.Dot(Rigidbody.velocity, transform.forward) : 0f;
        public virtual Transform MotionAnchor => transform;

        public abstract void CaptureState();
        public abstract void Attach(Transform mountAnchor, MountSeat seat);
        public abstract void Detach(Vector3 exitWorldPosition, Quaternion exitWorldRotation);
        public abstract void RestoreState();
    }
}
