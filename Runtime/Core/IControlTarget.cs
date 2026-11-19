using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Core interface for any entity that can receive local gameplay control authority.
    /// Does not depend on Rigidbody, wheels, motors, or specific third-party controller scripts.
    /// </summary>
    public interface IControlTarget
    {
        string TargetId { get; }
        GameObject TargetGameObject { get; }
        Transform Transform { get; }
        bool IsValid { get; }
        bool HasLease { get; }

        bool AcquireControlLease(long transactionId);
        void ReleaseControlLease(long transactionId);

        T GetCapability<T>() where T : class, IControlCapability;

        void NeutralizeInputs();
        void SetInputActive(bool active);
    }
}
