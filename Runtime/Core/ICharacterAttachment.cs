using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Contract for managing character attachment, quiescence, hierarchy, and state restoration during mounting.
    /// Preserves character lifetime, health, and identity while disabling dynamic motor/physics.
    /// </summary>
    public interface ICharacterAttachment : IControlCapability
    {
        void Attach(Transform mountAnchor, MountSeat seat);
        void Detach(Vector3 exitWorldPosition, Quaternion exitWorldRotation);
        void CaptureState();
        void RestoreState();
    }
}
